// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.FileBasedPrograms;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Cli.Utils.Extensions;

namespace Microsoft.DotNet.Cli.Commands.Run;

/// <summary>
/// Used to build a virtual project file in memory to support <c>dotnet run file.cs</c>.
/// </summary>
internal sealed class VirtualProjectBuildingCommand
{
    internal const string TargetFramework = "net10.0";

    /// <summary>
    /// A file put into the artifacts directory when build starts.
    /// It contains full path to the original source file to allow tracking down the input corresponding to the output.
    /// It is also used to check whether the previous build has failed (when it is newer than the <see cref="BuildSuccessCacheFileName"/>).
    /// </summary>
    private const string BuildStartCacheFileName = "build-start.cache";

    /// <summary>
    /// A file written in the artifacts directory on successful builds used to determine whether a re-build is needed.
    /// </summary>
    private const string BuildSuccessCacheFileName = "build-success.cache";

    private static readonly EnumerationOptions s_csEnumerationOptions = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = true,
    };

    private static readonly ImmutableArray<string> s_implicitBuildFileNames =
    [
        "global.json",

        // All these casings are recognized on case-sensitive platforms:
        // https://github.com/NuGet/NuGet.Client/blob/ab6b96fd9ba07ed3bf629ee389799ca4fb9a20fb/src/NuGet.Core/NuGet.Configuration/Settings/Settings.cs#L32-L37
        "nuget.config",
        "NuGet.config",
        "NuGet.Config",

        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "Directory.Build.rsp",
        "MSBuild.rsp",
    ];

    private string? _projectFileText;

    public Dictionary<string, string> GlobalProperties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public required string EntryPointFileFullPath { get; init; }

    public int Execute(string[] binaryLoggerArgs, ILogger consoleLogger, bool noRestore, bool noCache)
    {
        var binaryLogger = GetBinaryLogger(binaryLoggerArgs);

        RunFileBuildCacheEntry cacheEntry;

        if (noCache)
        {
            if (noRestore)
            {
                throw new GracefulException(CliCommandStrings.InvalidOptionCombination, RunCommandParser.NoCacheOption.Name, RunCommandParser.NoRestoreOption.Name);
            }

            cacheEntry = ComputeCacheEntry(out _);
        }
        else if (!NeedsToBuild(out cacheEntry))
        {
            if (binaryLogger is not null)
            {
                Reporter.Output.WriteLine(CliCommandStrings.NoBinaryLogBecauseUpToDate.Yellow());
            }

            PrepareProjectInstance();

            return 0;
        }

        MarkBuildStart();

        Dictionary<string, string?> savedEnvironmentVariables = [];
        try
        {
            // Set environment variables.
            foreach (var (key, value) in MSBuildForwardingAppWithoutLogging.GetMSBuildRequiredEnvironmentVariables())
            {
                savedEnvironmentVariables[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }

            // Set up MSBuild.
            ReadOnlySpan<ILogger> binaryLoggers = binaryLogger is null ? [] : [binaryLogger];
            var projectCollection = new ProjectCollection(
                GlobalProperties,
                [.. binaryLoggers, consoleLogger],
                ToolsetDefinitionLocations.Default);
            var parameters = new BuildParameters(projectCollection)
            {
                Loggers = projectCollection.Loggers,
                LogTaskInputs = binaryLoggers.Length != 0,
            };
            BuildManager.DefaultBuildManager.BeginBuild(parameters);

            PrepareProjectInstance();

            // Do a restore first (equivalent to MSBuild's "implicit restore", i.e., `/restore`).
            // See https://github.com/dotnet/msbuild/blob/a1c2e7402ef0abe36bf493e395b04dd2cb1b3540/src/MSBuild/XMake.cs#L1838
            // and https://github.com/dotnet/msbuild/issues/11519.
            if (!noRestore)
            {
                var restoreRequest = new BuildRequestData(
                    CreateProjectInstance(projectCollection, addGlobalProperties: static (globalProperties) =>
                    {
                        globalProperties["MSBuildRestoreSessionId"] = Guid.NewGuid().ToString("D");
                        globalProperties["MSBuildIsRestoring"] = bool.TrueString;
                    }),
                    targetsToBuild: ["Restore"],
                    hostServices: null,
                    BuildRequestDataFlags.ClearCachesAfterBuild | BuildRequestDataFlags.SkipNonexistentTargets | BuildRequestDataFlags.IgnoreMissingEmptyAndInvalidImports | BuildRequestDataFlags.FailOnUnresolvedSdk);
                var restoreResult = BuildManager.DefaultBuildManager.BuildRequest(restoreRequest);
                if (restoreResult.OverallResult != BuildResultCode.Success)
                {
                    return 1;
                }
            }

            // Then do a build.
            var buildRequest = new BuildRequestData(
                CreateProjectInstance(projectCollection),
                targetsToBuild: ["Build"]);
            var buildResult = BuildManager.DefaultBuildManager.BuildRequest(buildRequest);
            if (buildResult.OverallResult != BuildResultCode.Success)
            {
                return 1;
            }

            BuildManager.DefaultBuildManager.EndBuild();

            MarkBuildSuccess(cacheEntry);

            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        finally
        {
            foreach (var (key, value) in savedEnvironmentVariables)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            binaryLogger?.Shutdown();
            consoleLogger.Shutdown();
        }

        static ILogger? GetBinaryLogger(string[] args)
        {
            // Like in MSBuild, only the last binary logger is used.
            for (int i = args.Length - 1; i >= 0; i--)
            {
                var arg = args[i];
                if (LoggerUtility.IsBinLogArgument(arg))
                {
                    return new BinaryLogger
                    {
                        Parameters = arg.IndexOf(':') is >= 0 and var index
                            ? arg[(index + 1)..]
                            : "msbuild.binlog",
                    };
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Compute current cache entry - we need to do this always:
    /// <list type="bullet">
    /// <item>if we can skip build, we still need to check everything in the cache entry (e.g., implicit build files)</item>
    /// <item>if we have to build, we need to have the cache entry to write it to the success cache file</item>
    /// </list>
    /// </summary>
    private RunFileBuildCacheEntry ComputeCacheEntry(out FileInfo entryPointFileInfo)
    {
        var cacheEntry = new RunFileBuildCacheEntry(GlobalProperties);
        entryPointFileInfo = new FileInfo(EntryPointFileFullPath);

        // Collect current implicit build files.
        DirectoryInfo? directory = entryPointFileInfo.Directory;
        while (directory != null)
        {
            foreach (var implicitBuildFileName in s_implicitBuildFileNames)
            {
                string implicitBuildFilePath = Path.Join(directory.FullName, implicitBuildFileName);
                var implicitBuildFileInfo = new FileInfo(implicitBuildFilePath);
                if (implicitBuildFileInfo.Exists)
                {
                    cacheEntry.ImplicitBuildFiles.Add(implicitBuildFilePath, implicitBuildFileInfo.LastWriteTimeUtc);
                }
            }

            directory = directory.Parent;
        }

        return cacheEntry;
    }

    private bool NeedsToBuild(out RunFileBuildCacheEntry cacheEntry)
    {
        cacheEntry = ComputeCacheEntry(out FileInfo entryPointFileInfo);

        // Check cache files.

        string artifactsDirectory = GetArtifactsPath();
        var successCacheFile = new FileInfo(Path.Join(artifactsDirectory, BuildSuccessCacheFileName));

        if (!successCacheFile.Exists)
        {
            Reporter.Verbose.WriteLine("Building because cache file does not exist: " + successCacheFile.FullName);
            return true;
        }

        var startCacheFile = new FileInfo(Path.Join(artifactsDirectory, BuildStartCacheFileName));
        if (!startCacheFile.Exists)
        {
            Reporter.Verbose.WriteLine("Building because start cache file does not exist: " + startCacheFile.FullName);
            return true;
        }

        if (startCacheFile.LastWriteTimeUtc > successCacheFile.LastWriteTimeUtc)
        {
            Reporter.Verbose.WriteLine("Building because start cache file is newer than success cache file (previous build likely failed): " + startCacheFile.FullName);
            return true;
        }

        var previousCacheEntry = DeserializeCacheEntry(successCacheFile);
        if (previousCacheEntry is null)
        {
            Reporter.Verbose.WriteLine("Building because previous cache entry could not be deserialized: " + successCacheFile.FullName);
            return true;
        }

        // Check that properties match.

        if (previousCacheEntry.GlobalProperties.Count != cacheEntry.GlobalProperties.Count)
        {
            Reporter.Verbose.WriteLine($"""
                Building because previous global properties count ({previousCacheEntry.GlobalProperties.Count}) does not match current count ({cacheEntry.GlobalProperties.Count}): {successCacheFile.FullName}
                """);
            return true;
        }

        foreach (var (key, value) in cacheEntry.GlobalProperties)
        {
            if (!previousCacheEntry.GlobalProperties.TryGetValue(key, out var otherValue) ||
                value != otherValue)
            {
                Reporter.Verbose.WriteLine($"""
                    Building because previous global property "{key}" ({otherValue}) does not match current ({value}): {successCacheFile.FullName}
                    """);
                return true;
            }
        }

        DateTime buildTimeUtc = successCacheFile.LastWriteTimeUtc;

        // Check that the source file is up to date.
        // If it does not exist, we also want to build.
        if (!entryPointFileInfo.Exists || entryPointFileInfo.LastWriteTimeUtc > buildTimeUtc)
        {
            Reporter.Verbose.WriteLine("Building because entry point file is missing or modified: " + entryPointFileInfo.FullName);
            return true;
        }

        // Check that implicit build files are up to date.
        foreach (var implicitBuildFilePath in previousCacheEntry.ImplicitBuildFiles.Keys)
        {
            var implicitBuildFileInfo = new FileInfo(implicitBuildFilePath);
            if (!implicitBuildFileInfo.Exists || implicitBuildFileInfo.LastWriteTimeUtc > buildTimeUtc)
            {
                Reporter.Verbose.WriteLine("Building because implicit build file is missing or modified: " + implicitBuildFileInfo.FullName);
                return true;
            }
        }

        // Check that no new implicit build files are present.
        foreach (var implicitBuildFilePath in cacheEntry.ImplicitBuildFiles.Keys)
        {
            if (!previousCacheEntry.ImplicitBuildFiles.ContainsKey(implicitBuildFilePath))
            {
                Reporter.Verbose.WriteLine("Building because new implicit build file is present: " + implicitBuildFilePath);
                return true;
            }
        }

        return false;

        static RunFileBuildCacheEntry? DeserializeCacheEntry(FileInfo cacheFile)
        {
            try
            {
                using var stream = File.Open(cacheFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                return JsonSerializer.Deserialize(stream, RunFileJsonSerializerContext.Default.RunFileBuildCacheEntry);
            }
            catch (Exception e)
            {
                Reporter.Verbose.WriteLine($"Failed to deserialize cache entry ({cacheFile.FullName}): {e.GetType().FullName}: {e.Message}");
                return null;
            }
        }
    }

    private void MarkBuildStart()
    {
        string directory = GetArtifactsPath();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, BuildStartCacheFileName), EntryPointFileFullPath);
    }

    private void MarkBuildSuccess(RunFileBuildCacheEntry cacheEntry)
    {
        string successCacheFile = Path.Join(GetArtifactsPath(), BuildSuccessCacheFileName);
        using var stream = File.Open(successCacheFile, FileMode.Create, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, cacheEntry, RunFileJsonSerializerContext.Default.RunFileBuildCacheEntry);
    }

    /// <summary>
    /// Needs to be called before the first call to <see cref="CreateProjectInstance(ProjectCollection)"/>.
    /// </summary>
    public VirtualProjectBuildingCommand PrepareProjectInstance()
    {
#pragma warning disable RSEXPERIMENTAL006 // 'FileBasedProgramProject' is experimental

        Debug.Assert(_projectFileText == null, $"{nameof(PrepareProjectInstance)} should not be called multiple times.");

        if (!HasTopLevelStatements(EntryPointFileFullPath))
        {
            throw new GracefulException(CliCommandStrings.NoTopLevelStatements, EntryPointFileFullPath);
        }

        // Parse directives in the entry-point file.
        var projectBuilder = new FileBasedProgramProjectBuilder();
        ParseDirectives(projectBuilder, EntryPointFileFullPath);

        // Discover other C# files.
        var entryFile = new FileInfo(EntryPointFileFullPath);
        var entryDirectory = entryFile.Directory!;
        var excluded = ImmutableArray.CreateBuilder<string>();
        foreach (var file in entryDirectory.EnumerateFiles("*.cs", s_csEnumerationOptions))
        {
            bool isTopLevel = entryDirectory.FullName.Equals(file.Directory!.FullName, StringComparison.OrdinalIgnoreCase);

            // Skip the current entry point.
            if (isTopLevel && entryFile.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HasTopLevelStatements(file.FullName))
            {
                if (!isTopLevel)
                {
                    throw new GracefulException(CliCommandStrings.EntryPointInNestedFolder, file.FullName);
                }

                // Exclude other entry points.
                excluded.Add(file.FullName);
            }
            else
            {
                // Parse directives from other non-entry-point files.
                ParseDirectives(projectBuilder, file.FullName);
            }
        }

        // Generate project file XML text.
        var project = projectBuilder.Build();
        var csprojWriter = new StringWriter();
        project.Emit(csprojWriter, new FileBasedProgramProjectOptions
        {
            TargetFramework = TargetFramework,
            ArtifactsPath = GetArtifactsPath(),
            ExcludeCompileItems = excluded.DrainToImmutable(),
        });
        _projectFileText = csprojWriter.ToString();

        return this;

        static bool HasTopLevelStatements(string fileFullPath)
        {
            var tree = ParseCSharp(fileFullPath);
            return tree.GetRoot().ChildNodes().OfType<GlobalStatementSyntax>().Any();
        }

        static CSharpSyntaxTree ParseCSharp(string fileFullPath)
        {
            using var stream = File.OpenRead(fileFullPath);
            return (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(SourceText.From(stream, Encoding.UTF8), path: fileFullPath);
        }

        static void ParseDirectives(FileBasedProgramProjectBuilder projectBuilder, string fileFullPath)
        {
            var diagnostics = projectBuilder.ParseDirectives(fileFullPath, LoadSourceText(fileFullPath), reportAllErrors: false);
            if (diagnostics.Length != 0)
            {
                throw new GracefulException(CliCommandStrings.RunFileInvalidDirectives, string.Join(Environment.NewLine, diagnostics));
            }
        }

#pragma warning restore RSEXPERIMENTAL006 // 'FileBasedProgramProject' is experimental
    }

    public ProjectInstance CreateProjectInstance(ProjectCollection projectCollection)
    {
        return CreateProjectInstance(projectCollection, addGlobalProperties: null);
    }

    private ProjectInstance CreateProjectInstance(
        ProjectCollection projectCollection,
        Action<IDictionary<string, string>>? addGlobalProperties)
    {
        var projectRoot = CreateProjectRootElement(projectCollection);

        var globalProperties = projectCollection.GlobalProperties;
        if (addGlobalProperties is not null)
        {
            globalProperties = new Dictionary<string, string>(projectCollection.GlobalProperties, StringComparer.OrdinalIgnoreCase);
            addGlobalProperties(globalProperties);
        }

        return ProjectInstance.FromProjectRootElement(projectRoot, new ProjectOptions
        {
            GlobalProperties = globalProperties,
        });

        ProjectRootElement CreateProjectRootElement(ProjectCollection projectCollection)
        {
            Debug.Assert(_projectFileText != null, $"{nameof(PrepareProjectInstance)} should have been called first.");

            using var reader = new StringReader(_projectFileText);
            using var xmlReader = XmlReader.Create(reader);
            var projectRoot = ProjectRootElement.Create(xmlReader, projectCollection);
            projectRoot.FullPath = Path.ChangeExtension(EntryPointFileFullPath, ".csproj");
            return projectRoot;
        }
    }

    public static string GetArtifactsPath(string entryPointFileFullPath)
    {
#pragma warning disable RSEXPERIMENTAL006 // 'FileBasedProgramProject' is experimental
        return FileBasedProgramProject.GetArtifactsPath(entryPointFileFullPath);
#pragma warning restore RSEXPERIMENTAL006 // 'FileBasedProgramProject' is experimental
    }

    private string GetArtifactsPath()
    {
        return GetArtifactsPath(EntryPointFileFullPath);
    }

    public static SourceText LoadSourceText(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return SourceText.From(stream, Encoding.UTF8);
    }

    public static bool IsValidEntryPointPath(string entryPointFilePath)
    {
        return entryPointFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(entryPointFilePath);
    }
}

internal sealed class RunFileBuildCacheEntry
{
    private static StringComparer GlobalPropertiesComparer => StringComparer.OrdinalIgnoreCase;
    private static StringComparer ImplicitBuildFilesComparer => StringComparer.Ordinal;

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> GlobalProperties { get; }

    /// <summary>
    /// Maps full path to <see cref="FileSystemInfo.LastWriteTimeUtc"/>.
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, DateTime> ImplicitBuildFiles { get; }

    [JsonConstructor]
    public RunFileBuildCacheEntry()
    {
        GlobalProperties = new(GlobalPropertiesComparer);
        ImplicitBuildFiles = new(ImplicitBuildFilesComparer);
    }

    public RunFileBuildCacheEntry(Dictionary<string, string> globalProperties)
    {
        Debug.Assert(globalProperties.Comparer == GlobalPropertiesComparer);
        GlobalProperties = globalProperties;
        ImplicitBuildFiles = new(ImplicitBuildFilesComparer);
    }
}

[JsonSerializable(typeof(RunFileBuildCacheEntry))]
internal partial class RunFileJsonSerializerContext : JsonSerializerContext;
