// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.DotNet.Cli.Commands.Run;
using Microsoft.DotNet.Cli.Utils;
using SclCommand = System.CommandLine.Command;

namespace Microsoft.DotNet.Cli;

internal static partial class AotRunCommand
{
    private const string BuildStartCacheFileName = "build-start.cache";
    private const string BuildSuccessCacheFileName = "build-success.cache";

    private static readonly (string Name, bool IsMSBuildFile)[] s_implicitBuildFiles =
    [
        ("global.json", false),
        ("nuget.config", false),
        ("NuGet.config", false),
        ("NuGet.Config", false),
        ("Directory.Build.props", true),
        ("Directory.Build.targets", true),
        ("Directory.Packages.props", true),
        ("Directory.Build.rsp", true),
        ("MSBuild.rsp", true),
    ];

    public static SclCommand Create()
    {
        var fileOption = new Option<string?>("--file") { Description = "Path to the file-based program." };
        var projectOption = new Option<string?>("--project");
        var noBuildOption = new Option<bool>("--no-build") { Arity = ArgumentArity.Zero };
        var noRestoreOption = new Option<bool>("--no-restore") { Arity = ArgumentArity.Zero };
        var noLaunchProfileOption = new Option<bool>("--no-launch-profile") { Arity = ArgumentArity.Zero };
        var launchProfileOption = new Option<string?>("--launch-profile", "-lp");
        var noCacheOption = new Option<bool>("--no-cache") { Arity = ArgumentArity.Zero };
        var applicationArguments = new Argument<string[]>("applicationArguments")
        {
            DefaultValueFactory = _ => [],
            Description = "Arguments passed to the application that is being run."
        };

        var runCommand = new SclCommand("run", "Run a file-based app.")
        {
            fileOption,
            projectOption,
            noBuildOption,
            noRestoreOption,
            noLaunchProfileOption,
            launchProfileOption,
            noCacheOption,
            applicationArguments,
        };

        runCommand.TreatUnmatchedTokensAsErrors = false;
        runCommand.SetAction(parseResult => Execute(
            fileOption,
            projectOption,
            noBuildOption,
            noRestoreOption,
            noLaunchProfileOption,
            launchProfileOption,
            noCacheOption,
            applicationArguments,
            parseResult));

        return runCommand;
    }

    private static int Execute(
        Option<string?> fileOption,
        Option<string?> projectOption,
        Option<bool> noBuildOption,
        Option<bool> noRestoreOption,
        Option<bool> noLaunchProfileOption,
        Option<string?> launchProfileOption,
        Option<bool> noCacheOption,
        Argument<string[]> applicationArguments,
        ParseResult parseResult)
    {
        if (parseResult.GetValue(projectOption) is not null ||
            parseResult.GetValue(noCacheOption) ||
            parseResult.GetValue(launchProfileOption) is { Length: > 0 })
        {
            return Parser.FallbackToManagedCli;
        }

        if (HasUnsupportedOptions(parseResult))
        {
            return Parser.FallbackToManagedCli;
        }

        var args = parseResult.GetValue(applicationArguments) ?? [];
        if (!TryResolveEntryPoint(parseResult.GetValue(fileOption), ref args, out string? entryPointFileFullPath))
        {
            return Parser.FallbackToManagedCli;
        }
        string entryPointFile = entryPointFileFullPath ?? throw new InvalidOperationException();

        if (parseResult.GetValue(noLaunchProfileOption) == false && HasLaunchSettings(entryPointFile))
        {
            return Parser.FallbackToManagedCli;
        }

        if (parseResult.GetValue(noBuildOption) && parseResult.GetValue(noRestoreOption) == false)
        {
            // The managed command treats --no-build as implying --no-restore. Keep that shape here.
            _ = noRestoreOption;
        }

        var artifactsPath = GetArtifactsPath(entryPointFile);
        var previousCacheEntry = ReadPreviousCacheEntry(artifactsPath);

        if (parseResult.GetValue(noBuildOption))
        {
            return TryRunFromCache(entryPointFile, artifactsPath, previousCacheEntry, args, out int exitCode)
                ? exitCode
                : Parser.FallbackToManagedCli;
        }

        if (RequiresMSBuild(entryPointFile, previousCacheEntry, out var implicitBuildFiles, out var exampleMSBuildFile))
        {
            return Parser.FallbackToManagedCli;
        }

        var currentCacheEntry = new AotRunFileBuildCacheEntry
        {
            BuildLevel = AotBuildLevel.Csc,
            GlobalProperties = new(StringComparer.OrdinalIgnoreCase),
            ImplicitBuildFiles = implicitBuildFiles,
            Directives = [],
            AdditionalSources = previousCacheEntry?.AdditionalSources ?? new(StringComparer.Ordinal),
            SdkVersion = Product.Version,
            RuntimeVersion = CSharpCompilerCommand.RuntimeVersion,
        };

        if (!NeedsToBuild(entryPointFile, artifactsPath, currentCacheEntry, previousCacheEntry))
        {
            return TryRunFromCache(entryPointFile, artifactsPath, previousCacheEntry, args, out int exitCode)
                ? exitCode
                : Parser.FallbackToManagedCli;
        }

        if (exampleMSBuildFile is not null)
        {
            return Parser.FallbackToManagedCli;
        }

        Directory.CreateDirectory(artifactsPath);
        File.WriteAllText(Path.Join(artifactsPath, BuildStartCacheFileName), entryPointFile);

        var cscArguments = CanReuseCscArguments(entryPointFile, artifactsPath, currentCacheEntry, previousCacheEntry)
            ? previousCacheEntry!.CscArguments.ToImmutableArray()
            : ImmutableArray<string>.Empty;

        currentCacheEntry.CscArguments = [.. cscArguments];
        currentCacheEntry.BuildResultFile = cscArguments.IsDefaultOrEmpty ? null : previousCacheEntry?.BuildResultFile;
        currentCacheEntry.Run = cscArguments.IsDefaultOrEmpty ? null : previousCacheEntry?.Run;

        int result = new CSharpCompilerCommand
        {
            EntryPointFileFullPath = entryPointFile,
            ArtifactsPath = artifactsPath,
            CanReuseAuxiliaryFiles = previousCacheEntry?.BuildLevel == AotBuildLevel.Csc,
            CscArguments = cscArguments,
            BuildResultFile = currentCacheEntry.BuildResultFile,
        }.Execute(out bool fallbackToNormalBuild);

        if (fallbackToNormalBuild)
        {
            return Parser.FallbackToManagedCli;
        }

        if (result != 0)
        {
            return result;
        }

        WriteSuccessCacheEntry(artifactsPath, currentCacheEntry);
        return RunCscBuiltProgram(entryPointFile, artifactsPath, args);
    }

    private static bool HasUnsupportedOptions(ParseResult parseResult)
    {
        foreach (var token in parseResult.Tokens.TakeWhile(static token => token.Type != TokenType.DoubleDash))
        {
            if (token.Type == TokenType.Option && token.Value is not "--file" and not "--no-build" and not "--no-restore" and not "--no-launch-profile")
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveEntryPoint(string? fileOption, ref string[] args, out string? entryPointFileFullPath)
    {
        if (fileOption is not null)
        {
            entryPointFileFullPath = Path.GetFullPath(fileOption);
            return IsValidEntryPointPath(entryPointFileFullPath);
        }

        if (Directory.GetFiles(Directory.GetCurrentDirectory(), "*.*proj").Length != 0)
        {
            entryPointFileFullPath = null;
            return false;
        }

        if (args is [{ } candidate, ..] && IsValidEntryPointPath(candidate))
        {
            entryPointFileFullPath = Path.GetFullPath(candidate);
            args = args[1..];
            return true;
        }

        entryPointFileFullPath = null;
        return false;
    }

    private static bool IsValidEntryPointPath(string entryPointFilePath)
    {
        if (!File.Exists(entryPointFilePath))
        {
            return false;
        }

        if (entryPointFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var stream = File.OpenRead(entryPointFilePath);
            return stream.ReadByte() == '#' && stream.ReadByte() == '!';
        }
        catch
        {
            return false;
        }
    }

    private static bool HasLaunchSettings(string? fileOption)
    {
        if (fileOption is null)
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(fileOption));
        return directory is not null && File.Exists(Path.Join(directory, "Properties", "launchSettings.json"));
    }

    private static bool RequiresMSBuild(
        string entryPointFileFullPath,
        AotRunFileBuildCacheEntry? previousCacheEntry,
        out HashSet<string> implicitBuildFiles,
        out string? exampleMSBuildFile)
    {
        implicitBuildFiles = new(StringComparer.Ordinal);
        exampleMSBuildFile = null;

        if (ContainsFileLevelDirective(entryPointFileFullPath))
        {
            return true;
        }

        CollectImplicitBuildFiles(new FileInfo(entryPointFileFullPath).Directory!, implicitBuildFiles, out exampleMSBuildFile);
        if (exampleMSBuildFile is not null)
        {
            return true;
        }

        if (previousCacheEntry?.GlobalProperties.Count > 0 || previousCacheEntry?.Directives.Length > 0)
        {
            return true;
        }

        return false;
    }

    private static bool ContainsFileLevelDirective(string entryPointFileFullPath)
    {
        foreach (var line in File.ReadLines(entryPointFileFullPath))
        {
            var trimmedLine = line.TrimStart();
            if (trimmedLine.StartsWith("#!", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmedLine.StartsWith("#:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectImplicitBuildFiles(DirectoryInfo startDirectory, HashSet<string> collectedPaths, out string? exampleMSBuildFile)
    {
        exampleMSBuildFile = null;
        for (DirectoryInfo? directory = startDirectory; directory is not null; directory = directory.Parent)
        {
            foreach (var implicitBuildFile in s_implicitBuildFiles)
            {
                string implicitBuildFilePath = Path.Join(directory.FullName, implicitBuildFile.Name);
                if (!File.Exists(implicitBuildFilePath))
                {
                    continue;
                }

                collectedPaths.Add(implicitBuildFilePath);
                if (implicitBuildFile.IsMSBuildFile && exampleMSBuildFile is null)
                {
                    exampleMSBuildFile = implicitBuildFilePath;
                }
            }
        }
    }

    private static bool NeedsToBuild(
        string entryPointFileFullPath,
        string artifactsPath,
        AotRunFileBuildCacheEntry currentCacheEntry,
        AotRunFileBuildCacheEntry? previousCacheEntry)
    {
        var successCacheFile = new FileInfo(Path.Join(artifactsPath, BuildSuccessCacheFileName));
        var startCacheFile = new FileInfo(Path.Join(artifactsPath, BuildStartCacheFileName));
        if (!successCacheFile.Exists || !startCacheFile.Exists || startCacheFile.LastWriteTimeUtc > successCacheFile.LastWriteTimeUtc)
        {
            return true;
        }

        if (previousCacheEntry is null ||
            previousCacheEntry.SdkVersion != currentCacheEntry.SdkVersion ||
            previousCacheEntry.RuntimeVersion != currentCacheEntry.RuntimeVersion ||
            previousCacheEntry.BuildLevel != AotBuildLevel.Csc && previousCacheEntry.Run is null)
        {
            return true;
        }

        if (!SetEquals(previousCacheEntry.ImplicitBuildFiles, currentCacheEntry.ImplicitBuildFiles))
        {
            return true;
        }

        DateTime buildTimeUtc = successCacheFile.LastWriteTimeUtc;
        if (ResolveLinkTargetOrSelf(new FileInfo(entryPointFileFullPath)).LastWriteTimeUtc > buildTimeUtc)
        {
            return true;
        }

        foreach (var implicitBuildFilePath in previousCacheEntry.ImplicitBuildFiles)
        {
            var implicitBuildFileInfo = ResolveLinkTargetOrSelf(new FileInfo(implicitBuildFilePath));
            if (!implicitBuildFileInfo.Exists || implicitBuildFileInfo.LastWriteTimeUtc > buildTimeUtc)
            {
                return true;
            }
        }

        foreach (var additionalSourcePath in previousCacheEntry.AdditionalSources)
        {
            var additionalSourceInfo = ResolveLinkTargetOrSelf(new FileInfo(additionalSourcePath));
            if (!additionalSourceInfo.Exists || additionalSourceInfo.LastWriteTimeUtc > buildTimeUtc)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryRunFromCache(
        string entryPointFileFullPath,
        string artifactsPath,
        AotRunFileBuildCacheEntry? previousCacheEntry,
        string[] applicationArgs,
        out int exitCode)
    {
        if (previousCacheEntry?.BuildLevel == AotBuildLevel.Csc)
        {
            exitCode = RunCscBuiltProgram(entryPointFileFullPath, artifactsPath, applicationArgs);
            return true;
        }

        if (previousCacheEntry?.Run is { } runProperties)
        {
            exitCode = RunFromProperties(runProperties, applicationArgs);
            return true;
        }

        exitCode = 0;
        return false;
    }

    private static bool CanReuseCscArguments(
        string entryPointFileFullPath,
        string artifactsPath,
        AotRunFileBuildCacheEntry currentCacheEntry,
        AotRunFileBuildCacheEntry? previousCacheEntry)
    {
        if (previousCacheEntry?.CscArguments is not { Length: > 0 } ||
            previousCacheEntry.Run is null ||
            previousCacheEntry.BuildResultFile is null ||
            previousCacheEntry.Directives.Length != currentCacheEntry.Directives.Length)
        {
            return false;
        }

        var successCacheFile = new FileInfo(Path.Join(artifactsPath, BuildSuccessCacheFileName));
        return successCacheFile.Exists && ResolveLinkTargetOrSelf(new FileInfo(entryPointFileFullPath)).LastWriteTimeUtc > successCacheFile.LastWriteTimeUtc;
    }

    private static int RunCscBuiltProgram(string entryPointFileFullPath, string artifactsPath, string[] applicationArgs)
    {
        string exePath = Path.Join(
            artifactsPath,
            "bin",
            "debug",
            Path.GetFileNameWithoutExtension(entryPointFileFullPath) + FileNameSuffixes.CurrentPlatform.Exe);

        return RunProcess(
            exePath,
            ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart(applicationArgs),
            workingDirectory: Path.GetDirectoryName(entryPointFileFullPath),
            runtimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            defaultAppHostRuntimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            targetFrameworkVersion: $"v{CSharpCompilerCommand.TargetFrameworkVersion}");
    }

    private static int RunFromProperties(AotRunProperties runProperties, string[] applicationArgs)
    {
        string arguments = runProperties.Arguments ?? string.Empty;
        if (applicationArgs.Length != 0)
        {
            arguments = string.IsNullOrEmpty(arguments)
                ? ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart(applicationArgs)
                : arguments + " " + ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart(applicationArgs);
        }

        return RunProcess(
            runProperties.Command,
            arguments,
            runProperties.WorkingDirectory,
            runProperties.RuntimeIdentifier,
            runProperties.DefaultAppHostRuntimeIdentifier,
            runProperties.TargetFrameworkVersion);
    }

    private static int RunProcess(
        string fileName,
        string arguments,
        string? workingDirectory,
        string runtimeIdentifier,
        string defaultAppHostRuntimeIdentifier,
        string targetFrameworkVersion)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            UseShellExecute = false,
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        string? rootVariableName = EnvironmentVariableNames.TryGetDotNetRootVariableName(runtimeIdentifier, defaultAppHostRuntimeIdentifier, targetFrameworkVersion);
        if (rootVariableName is not null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(rootVariableName)))
        {
            startInfo.Environment[rootVariableName] = GetDotNetRootPath();
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return 1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    private static AotRunFileBuildCacheEntry? ReadPreviousCacheEntry(string artifactsPath)
    {
        try
        {
            using var stream = File.Open(Path.Join(artifactsPath, BuildSuccessCacheFileName), FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize(stream, AotRunJsonSerializerContext.Default.AotRunFileBuildCacheEntry);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteSuccessCacheEntry(string artifactsPath, AotRunFileBuildCacheEntry cacheEntry)
    {
        using var stream = File.Open(Path.Join(artifactsPath, BuildSuccessCacheFileName), FileMode.Create, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, cacheEntry, AotRunJsonSerializerContext.Default.AotRunFileBuildCacheEntry);
    }

    private static string GetArtifactsPath(string entryPointFileFullPath)
    {
        string fileName = Path.GetFileNameWithoutExtension(entryPointFileFullPath);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entryPointFileFullPath.ToUpperInvariant())));
        return Path.Join(GetTempSubdirectory(), $"{fileName}-{hash}");
    }

    private static string GetTempSubdirectory()
    {
        string directory = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.GetTempPath()
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return Path.Join(directory, "dotnet", "runfile");
    }

    private static string GetDotNetRootPath()
        => AotHostContext.DotNetRoot;

    private static bool SetEquals(HashSet<string> left, HashSet<string> right)
        => left.Count == right.Count && left.All(right.Contains);

    private static FileSystemInfo ResolveLinkTargetOrSelf(FileSystemInfo fileSystemInfo)
    {
        if (!fileSystemInfo.Exists)
        {
            return fileSystemInfo;
        }

        return fileSystemInfo.ResolveLinkTarget(returnFinalTarget: true) ?? fileSystemInfo;
    }

    private sealed class AotRunFileBuildCacheEntry
    {
        public Dictionary<string, string> GlobalProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ImplicitBuildFiles { get; set; } = new(StringComparer.Ordinal);
        public string[] Directives { get; set; } = [];
        public HashSet<string> AdditionalSources { get; set; } = new(StringComparer.Ordinal);
        public AotBuildLevel BuildLevel { get; set; }
        public string? SdkVersion { get; set; }
        public string? RuntimeVersion { get; set; }
        public AotRunProperties? Run { get; set; }
        public string[] CscArguments { get; set; } = [];
        public string? BuildResultFile { get; set; }
    }

    private sealed class AotRunProperties
    {
        public string Command { get; set; } = string.Empty;
        public string? Arguments { get; set; }
        public string? WorkingDirectory { get; set; }
        public string RuntimeIdentifier { get; set; } = string.Empty;
        public string DefaultAppHostRuntimeIdentifier { get; set; } = string.Empty;
        public string TargetFrameworkVersion { get; set; } = string.Empty;
    }

    private enum AotBuildLevel
    {
        None,
        Csc,
        All,
    }

    [JsonSerializable(typeof(AotRunFileBuildCacheEntry))]
    private partial class AotRunJsonSerializerContext : JsonSerializerContext;
}
