// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Collections.Immutable;
using System.Diagnostics;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

        CacheInfo cache;

        if (noCache)
        {
            if (noRestore)
            {
                throw new GracefulException(CliCommandStrings.InvalidOptionCombination, RunCommandParser.NoCacheOption.Name, RunCommandParser.NoRestoreOption.Name);
            }

            cache = ComputeCacheEntry();
        }
        else if (!NeedsToBuild(out cache))
        {
            if (binaryLogger is not null)
            {
                Reporter.Output.WriteLine(CliCommandStrings.NoBinaryLogBecauseUpToDate.Yellow());
            }

            PrepareProjectInstance(cache);

            return 0;
        }

        MarkBuildStart(cache);

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

            PrepareProjectInstance(cache);

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

            MarkBuildSuccess(cache);

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
    /// Common info needed by <see cref="ComputeCacheEntry"/> but also later stages.
    /// </summary>
    private sealed class CacheInfo
    {
        public required FileInfo EntryPointFile { get; init; }
        public required string ArtifactsDirectory { get; init; }
        public required FileInfo SuccessCacheFile { get; init; }
        public required RunFileBuildCacheEntry? PreviousEntry { get; init; }
        public required RunFileBuildCacheEntry CurrentEntry { get; init; }
        public required ImmutableArray<OtherSourceFile> OtherSources { get; init; }
    }

    /// <summary>
    /// Compute current <see cref="RunFileBuildCacheEntry"/> - we need to do this always:
    /// <list type="bullet">
    /// <item>if we can skip build, we still need to check everything in the cache entry (e.g., implicit build files)</item>
    /// <item>if we have to build, we need to have the cache entry to write it to the success cache file</item>
    /// </list>
    /// </summary>
    private CacheInfo ComputeCacheEntry()
    {
        string artifactsDirectory = GetArtifactsPath();
        var successCacheFile = new FileInfo(Path.Join(artifactsDirectory, BuildSuccessCacheFileName));
        DateTime? buildTimeUtcOpt = !successCacheFile.Exists ? null : successCacheFile.LastWriteTimeUtc;

        var previousCacheEntry = DeserializeCacheEntry(successCacheFile);

        var cacheEntry = new RunFileBuildCacheEntry(GlobalProperties);
        var entryPointFile = new FileInfo(EntryPointFileFullPath);

        // Collect current other source files.
        var otherSources = ImmutableArray.CreateBuilder<OtherSourceFile>(previousCacheEntry?.OtherSources.Count ?? 8);
        foreach (var item in FindOtherFiles(entryPointFile: entryPointFile, entryDirectory: null))
        {
            OtherSourceFile otherSource;

            // No need to parse the file if it hasn't changed since last time.
            if (buildTimeUtcOpt is { } buildTimeUtc &&
                previousCacheEntry?.OtherSources.TryGetValue(item.File.FullName, out var previous) == true &&
                item.File.LastWriteTimeUtc <= buildTimeUtc)
            {
                otherSource = new OtherSourceFile
                {
                    Path = item.File.FullName,
                    IsTopLevel = item.IsTopLevel,
                    IsEntryPoint = previous.IsEntryPoint,
                };
            }
            else
            {
                otherSource = ToOtherSourceFile(item);
            }

            otherSources.Add(otherSource);
            cacheEntry.OtherSources.Add(item.File.FullName, new() { IsEntryPoint = otherSource.IsEntryPoint });
        }

        // Collect current implicit build files.
        for (DirectoryInfo? directory = entryPointFile.Directory; directory != null; directory = directory.Parent)
        {
            foreach (var implicitBuildFileName in s_implicitBuildFileNames)
            {
                string implicitBuildFilePath = Path.Join(directory.FullName, implicitBuildFileName);
                if (File.Exists(implicitBuildFilePath))
                {
                    cacheEntry.ImplicitBuildFiles.Add(implicitBuildFilePath);
                }
            }
        }

        return new CacheInfo
        {
            EntryPointFile = entryPointFile,
            ArtifactsDirectory = artifactsDirectory,
            SuccessCacheFile = successCacheFile,
            PreviousEntry = previousCacheEntry,
            CurrentEntry = cacheEntry,
            OtherSources = otherSources.DrainToImmutable(),
        };

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

    private bool NeedsToBuild(out CacheInfo cache)
    {
        cache = ComputeCacheEntry();

        // Check cache files.

        string artifactsDirectory = cache.ArtifactsDirectory;
        var successCacheFile = cache.SuccessCacheFile;

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

        DateTime buildTimeUtc = successCacheFile.LastWriteTimeUtc;
        if (startCacheFile.LastWriteTimeUtc > buildTimeUtc)
        {
            Reporter.Verbose.WriteLine("Building because start cache file is newer than success cache file (previous build likely failed): " + startCacheFile.FullName);
            return true;
        }

        var previousCacheEntry = cache.PreviousEntry;
        if (previousCacheEntry is null)
        {
            Reporter.Verbose.WriteLine("Building because previous cache entry could not be deserialized: " + successCacheFile.FullName);
            return true;
        }

        // Check that properties match.

        var currentEntry = cache.CurrentEntry;
        if (previousCacheEntry.GlobalProperties.Count != currentEntry.GlobalProperties.Count)
        {
            Reporter.Verbose.WriteLine($"""
                Building because previous global properties count ({previousCacheEntry.GlobalProperties.Count}) does not match current count ({currentEntry.GlobalProperties.Count}): {successCacheFile.FullName}
                """);
            return true;
        }

        foreach (var (key, value) in currentEntry.GlobalProperties)
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

        // Check that the entry-point file is up to date.
        // If it does not exist, we also want to build.
        var entryPointFile = cache.EntryPointFile;
        if (!entryPointFile.Exists || entryPointFile.LastWriteTimeUtc > buildTimeUtc)
        {
            Reporter.Verbose.WriteLine("Building because entry point file is missing or modified: " + entryPointFile.FullName);
            return true;
        }

        // Check that other source files are up to date.
        foreach (var (otherSourceFilePath, otherSource) in previousCacheEntry.OtherSources)
        {
            if (!otherSource.IsEntryPoint)
            {
                var otherSourceFile = new FileInfo(otherSourceFilePath);
                if (!otherSourceFile.Exists || otherSourceFile.LastWriteTimeUtc > buildTimeUtc)
                {
                    Reporter.Verbose.WriteLine("Building because other source file is missing or modified: " + otherSourceFile.FullName);
                    return true;
                }
            }
        }

        // Check that no new other source files are present.
        foreach (var (otherSourceFilePath, otherSource) in currentEntry.OtherSources)
        {
            if (!otherSource.IsEntryPoint && !previousCacheEntry.OtherSources.ContainsKey(otherSourceFilePath))
            {
                Reporter.Verbose.WriteLine("Building because new other source file is present: " + otherSourceFilePath);
                return true;
            }
        }

        // Check that implicit build files are up to date.
        foreach (var implicitBuildFilePath in previousCacheEntry.ImplicitBuildFiles)
        {
            var implicitBuildFileInfo = new FileInfo(implicitBuildFilePath);
            if (!implicitBuildFileInfo.Exists || implicitBuildFileInfo.LastWriteTimeUtc > buildTimeUtc)
            {
                Reporter.Verbose.WriteLine("Building because implicit build file is missing or modified: " + implicitBuildFileInfo.FullName);
                return true;
            }
        }

        // Check that no new implicit build files are present.
        foreach (var implicitBuildFilePath in currentEntry.ImplicitBuildFiles)
        {
            if (!previousCacheEntry.ImplicitBuildFiles.Contains(implicitBuildFilePath))
            {
                Reporter.Verbose.WriteLine("Building because new implicit build file is present: " + implicitBuildFilePath);
                return true;
            }
        }

        // If everything is up to date, reuse project file text.
        currentEntry.ProjectFileText = previousCacheEntry?.ProjectFileText;

        return false;
    }

    private static void MarkBuildStart(CacheInfo cache)
    {
        string directory = cache.ArtifactsDirectory;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, BuildStartCacheFileName), cache.EntryPointFile.FullName);
    }

    private static void MarkBuildSuccess(CacheInfo cache)
    {
        string successCacheFile = Path.Join(cache.ArtifactsDirectory, BuildSuccessCacheFileName);
        using var stream = File.Open(successCacheFile, FileMode.Create, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, cache.CurrentEntry, RunFileJsonSerializerContext.Default.RunFileBuildCacheEntry);
    }

    /// <summary>
    /// Needs to be called before the first call to <see cref="CreateProjectInstance(ProjectCollection)"/>.
    /// </summary>
    public VirtualProjectBuildingCommand PrepareProjectInstance() => PrepareProjectInstance(cache: null);

    private VirtualProjectBuildingCommand PrepareProjectInstance(CacheInfo? cache)
    {
        Debug.Assert(_projectFileText == null, $"{nameof(PrepareProjectInstance)} should not be called multiple times.");

        if (cache?.CurrentEntry.ProjectFileText is { } projectFileText)
        {
            _projectFileText = projectFileText;
            return this;
        }

        DiscoverOtherFiles(
            entryPointFile: LoadSourceFile(EntryPointFileFullPath),
            entryDirectory: null,
            cache,
            reportAllDirectiveErrors: false,
            otherEntryPoints: out var otherEntryPoints,
            allFiles: out _,
            sortedDirectives: out var sortedDirectives);

        var csprojWriter = new StringWriter();

        WriteProjectFile(
            writer: csprojWriter,
            directives: sortedDirectives,
            isVirtualProject: true,
            artifactsPath: GetArtifactsPath(),
            excludeCompileItems: otherEntryPoints.Select(f => f.Path).ToImmutableArray());

        projectFileText = csprojWriter.ToString();
        if (cache != null) cache.CurrentEntry.ProjectFileText = projectFileText;
        _projectFileText = projectFileText;

        return this;
    }

    public static void DiscoverOtherFiles(
        SourceFile? entryPointFile,
        DirectoryInfo? entryDirectory,
        bool reportAllDirectiveErrors,
        out ImmutableArray<SourceFile> otherEntryPoints,
        out IReadOnlyDictionary<string, (SourceFile File, ImmutableArray<CSharpDirective> Directives)> allFiles,
        out ImmutableArray<CSharpDirective> sortedDirectives)
    {
        DiscoverOtherFiles(
            entryPointFile: entryPointFile,
            entryDirectory: entryDirectory,
            cache: null,
            reportAllDirectiveErrors: reportAllDirectiveErrors,
            otherEntryPoints: out otherEntryPoints,
            allFiles: out allFiles,
            sortedDirectives: out sortedDirectives);
    }

    private static void DiscoverOtherFiles(
        SourceFile? entryPointFile,
        DirectoryInfo? entryDirectory,
        CacheInfo? cache,
        bool reportAllDirectiveErrors,
        out ImmutableArray<SourceFile> otherEntryPoints,
        out IReadOnlyDictionary<string, (SourceFile File, ImmutableArray<CSharpDirective> Directives)> allFiles,
        out ImmutableArray<CSharpDirective> sortedDirectives)
    {
        // Sorted by file path to get deterministic results.
        var allFilesBuilder = new SortedDictionary<string, (SourceFile File, ImmutableArray<CSharpDirective> Directives)>(StringComparer.Ordinal);

        // Process entry-point file if provided.
        if (entryPointFile is { } entryPointFileValue)
        {
            if (!HasTopLevelStatements(entryPointFileValue))
            {
                throw new GracefulException(CliCommandStrings.NoTopLevelStatements, entryPointFileValue.Path);
            }

            // Parse directives in the entry-point file.
            allFilesBuilder.Add(entryPointFileValue.Path, (entryPointFileValue, FindDirectives(entryPointFileValue, reportErrors: reportAllDirectiveErrors)));
        }

        // Discover other C# files.
        var otherEntryPointsBuilder = ImmutableArray.CreateBuilder<SourceFile>();
        var otherSources = cache?.OtherSources ?? FindOtherFiles(
            entryPointFile: entryPointFile?.GetFileInfo(),
            entryDirectory: entryDirectory)
            .Select(ToOtherSourceFile);
        foreach (var other in otherSources)
        {
            var file = other.GetOrLoadFile();

            if (other.IsEntryPoint)
            {
                if (!other.IsTopLevel)
                {
                    throw new GracefulException(CliCommandStrings.EntryPointInNestedFolder, file.Path);
                }

                // Exclude other entry points.
                otherEntryPointsBuilder.Add(file);
            }
            else
            {
                // Parse directives from other non-entry-point files.
                allFilesBuilder.Add(file.Path, (file, FindDirectives(file, reportErrors: reportAllDirectiveErrors)));
            }
        }

        otherEntryPoints = otherEntryPointsBuilder.DrainToImmutable();
        allFiles = allFilesBuilder;
        sortedDirectives = allFilesBuilder.Values.SelectMany(x => x.Directives).ToImmutableArray();
    }

    private static bool HasTopLevelStatements(SourceFile file)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(file.Text, path: file.Path);
        return tree.GetRoot().ChildNodes().OfType<GlobalStatementSyntax>().Any();
    }

    /// <summary>
    /// Discovers inputs that can be used to create <see cref="OtherSourceFile"/>s (via <see cref="ToOtherSourceFile"/>).
    /// </summary>
    private static IEnumerable<(FileInfo File, bool IsTopLevel)> FindOtherFiles(FileInfo? entryPointFile, DirectoryInfo? entryDirectory)
    {
        entryDirectory ??= entryPointFile?.Directory;
        Debug.Assert(entryDirectory != null);
        var files = entryDirectory.EnumerateFiles("*.cs", s_csEnumerationOptions)
            .OrderBy(f => f.FullName, StringComparer.Ordinal);
        foreach (var file in files)
        {
            // Skip the current entry point (FileInfo.FullName is a normalized path, so we can compare it).
            if (entryPointFile?.FullName.Equals(file.FullName, StringComparison.Ordinal) == true)
            {
                continue;
            }

            bool isTopLevel = entryDirectory.FullName.Equals(file.Directory!.FullName, StringComparison.Ordinal);

            yield return (File: file, IsTopLevel: isTopLevel);
        }
    }

    private static OtherSourceFile ToOtherSourceFile((FileInfo File, bool IsTopLevel) input)
    {
        SourceFile file = LoadSourceFile(input.File.FullName);

        bool isEntryPoint = HasTopLevelStatements(file);

        return new OtherSourceFile
        {
            Path = file.Path,
            Text = file.Text,
            IsEntryPoint = isEntryPoint,
            IsTopLevel = input.IsTopLevel,
        };
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

    private string GetArtifactsPath() => GetArtifactsPath(EntryPointFileFullPath);

    // internal for testing
    internal static string GetArtifactsPath(string entryPointFileFullPath)
    {
        // We want a location where permissions are expected to be restricted to the current user.
        string directory = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.GetTempPath()
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Include entry point file name so the directory name is not completely opaque.
        string fileName = Path.GetFileNameWithoutExtension(entryPointFileFullPath);
        string hash = Sha256Hasher.HashWithNormalizedCasing(entryPointFileFullPath);
        string directoryName = $"{fileName}-{hash}";

        return Path.Join(directory, "dotnet", "runfile", directoryName);
    }

    public static void WriteProjectFile(
        TextWriter writer,
        ImmutableArray<CSharpDirective> directives,
        bool isVirtualProject,
        string? artifactsPath = null,
        ImmutableArray<string> excludeCompileItems = default)
    {
        int processedDirectives = 0;

        var sdkDirectives = directives.OfType<CSharpDirective.Sdk>();
        var propertyDirectives = directives.OfType<CSharpDirective.Property>();
        var packageDirectives = directives.OfType<CSharpDirective.Package>();

        string sdkValue = "Microsoft.NET.Sdk";

        if (sdkDirectives.FirstOrDefault() is { } firstSdk)
        {
            sdkValue = firstSdk.ToSlashDelimitedString();
            processedDirectives++;
        }

        if (isVirtualProject)
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(artifactsPath));

            writer.WriteLine($"""
                <Project>

                  <PropertyGroup>
                    <IncludeProjectNameInArtifactsPaths>false</IncludeProjectNameInArtifactsPaths>
                    <ArtifactsPath>{EscapeValue(artifactsPath)}</ArtifactsPath>
                  </PropertyGroup>

                  <!-- We need to explicitly import Sdk props/targets so we can override the targets below. -->
                  <Import Project="Sdk.props" Sdk="{EscapeValue(sdkValue)}" />
                """);
        }
        else
        {
            writer.WriteLine($"""
                <Project Sdk="{EscapeValue(sdkValue)}">

                """);
        }

        foreach (var sdk in sdkDirectives.Skip(1))
        {
            if (isVirtualProject)
            {
                writer.WriteLine($"""
                      <Import Project="Sdk.props" Sdk="{EscapeValue(sdk.ToSlashDelimitedString())}" />
                    """);
            }
            else if (sdk.Version is null)
            {
                writer.WriteLine($"""
                      <Sdk Name="{EscapeValue(sdk.Name)}" />
                    """);
            }
            else
            {
                writer.WriteLine($"""
                      <Sdk Name="{EscapeValue(sdk.Name)}" Version="{EscapeValue(sdk.Version)}" />
                    """);
            }

            processedDirectives++;
        }

        if (processedDirectives > 1)
        {
            writer.WriteLine();
        }

        // Kept in sync with the default `dotnet new console` project file (enforced by `DotnetProjectAddTests.SameAsTemplate`).
        writer.WriteLine($"""
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            """);

        if (propertyDirectives.Any())
        {
            writer.WriteLine("""

                  <PropertyGroup>
                """);

            foreach (var property in propertyDirectives)
            {
                writer.WriteLine($"""
                        <{property.Name}>{EscapeValue(property.Value)}</{property.Name}>
                    """);

                processedDirectives++;
            }

            writer.WriteLine("  </PropertyGroup>");
        }

        if (isVirtualProject)
        {
            // After `#:property` directives so they don't override this.
            writer.WriteLine("""

                  <PropertyGroup>
                    <Features>$(Features);FileBasedProgram</Features>
                  </PropertyGroup>
                """);
        }

        if (packageDirectives.Any())
        {
            writer.WriteLine("""

                  <ItemGroup>
                """);

            foreach (var package in packageDirectives)
            {
                if (package.Version is null)
                {
                    writer.WriteLine($"""
                            <PackageReference Include="{EscapeValue(package.Name)}" />
                        """);
                }
                else
                {
                    writer.WriteLine($"""
                            <PackageReference Include="{EscapeValue(package.Name)}" Version="{EscapeValue(package.Version)}" />
                        """);
                }

                processedDirectives++;
            }

            writer.WriteLine("  </ItemGroup>");
        }

        Debug.Assert(processedDirectives + directives.OfType<CSharpDirective.Shebang>().Count() == directives.Length);

        if (isVirtualProject)
        {
            if (!excludeCompileItems.IsEmpty)
            {
                writer.WriteLine("""

                      <ItemGroup>
                    """);

                foreach (var excludeCompileItem in excludeCompileItems)
                {
                    writer.WriteLine($"""
                            <Compile Remove="{EscapeValue(excludeCompileItem)}" />
                        """);
                }

                writer.WriteLine($"""
                      </ItemGroup>

                    """);
            }

            foreach (var sdk in sdkDirectives)
            {
                writer.WriteLine($"""
                      <Import Project="Sdk.targets" Sdk="{EscapeValue(sdk.ToSlashDelimitedString())}" />
                    """);
            }

            if (!sdkDirectives.Any())
            {
                Debug.Assert(sdkValue == "Microsoft.NET.Sdk");
                writer.WriteLine("""
                      <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
                    """);
            }

            writer.WriteLine("""

                  <!--
                    Override targets which don't work with project files that are not present on disk.
                    See https://github.com/NuGet/Home/issues/14148.
                  -->

                  <Target Name="_FilterRestoreGraphProjectInputItems"
                          DependsOnTargets="_LoadRestoreGraphEntryPoints"
                          Returns="@(FilteredRestoreGraphProjectInputItems)">
                    <ItemGroup>
                      <FilteredRestoreGraphProjectInputItems Include="@(RestoreGraphProjectInputItems)" />
                    </ItemGroup>
                  </Target>

                  <Target Name="_GetAllRestoreProjectPathItems"
                          DependsOnTargets="_FilterRestoreGraphProjectInputItems"
                          Returns="@(_RestoreProjectPathItems)">
                    <ItemGroup>
                      <_RestoreProjectPathItems Include="@(FilteredRestoreGraphProjectInputItems)" />
                    </ItemGroup>
                  </Target>

                  <Target Name="_GenerateRestoreGraph"
                          DependsOnTargets="_FilterRestoreGraphProjectInputItems;_GetAllRestoreProjectPathItems;_GenerateRestoreGraphProjectEntry;_GenerateProjectRestoreGraph"
                          Returns="@(_RestoreGraphEntry)">
                    <!-- Output from dependency _GenerateRestoreGraphProjectEntry and _GenerateProjectRestoreGraph -->
                  </Target>
                """);
        }

        writer.WriteLine("""

            </Project>
            """);

        static string EscapeValue(string value) => SecurityElement.Escape(value);
    }

#pragma warning disable RSEXPERIMENTAL003 // 'SyntaxTokenParser' is experimental
    public static ImmutableArray<CSharpDirective> FindDirectives(SourceFile sourceFile, bool reportErrors)
    {
        var builder = ImmutableArray.CreateBuilder<CSharpDirective>();
        SyntaxTokenParser tokenizer = SyntaxFactory.CreateTokenParser(sourceFile.Text,
            CSharpParseOptions.Default.WithFeatures([new("FileBasedProgram", "true")]));

        var result = tokenizer.ParseLeadingTrivia();
        TextSpan previousWhiteSpaceSpan = default;
        foreach (var trivia in result.Token.LeadingTrivia)
        {
            // Stop when the trivia contains an error (e.g., because it's after #if).
            if (trivia.ContainsDiagnostics)
            {
                break;
            }

            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                Debug.Assert(previousWhiteSpaceSpan.IsEmpty);
                previousWhiteSpaceSpan = trivia.FullSpan;
                continue;
            }

            if (trivia.IsKind(SyntaxKind.ShebangDirectiveTrivia))
            {
                TextSpan span = getFullSpan(previousWhiteSpaceSpan, trivia);

                builder.Add(new CSharpDirective.Shebang { Span = span });
            }
            else if (trivia.IsKind(SyntaxKind.IgnoredDirectiveTrivia))
            {
                TextSpan span = getFullSpan(previousWhiteSpaceSpan, trivia);

                var message = trivia.GetStructure() is IgnoredDirectiveTriviaSyntax { Content: { RawKind: (int)SyntaxKind.StringLiteralToken } content }
                    ? content.Text.AsSpan().Trim()
                    : "";
                var parts = Patterns.Whitespace.EnumerateSplits(message, 2);
                var name = parts.MoveNext() ? message[parts.Current] : default;
                var value = parts.MoveNext() ? message[parts.Current] : default;
                Debug.Assert(!parts.MoveNext());
                builder.Add(CSharpDirective.Parse(sourceFile, span, name.ToString(), value.ToString()));
            }

            previousWhiteSpaceSpan = default;
        }

        // In conversion mode, we want to report errors for any invalid directives in the rest of the file
        // so users don't end up with invalid directives in the converted project.
        if (reportErrors)
        {
            tokenizer.ResetTo(result);

            do
            {
                result = tokenizer.ParseNextToken();

                foreach (var trivia in result.Token.LeadingTrivia)
                {
                    reportErrorFor(sourceFile, trivia);
                }

                foreach (var trivia in result.Token.TrailingTrivia)
                {
                    reportErrorFor(sourceFile, trivia);
                }
            }
            while (!result.Token.IsKind(SyntaxKind.EndOfFileToken));
        }

        // The result should be ordered by source location, RemoveDirectivesFromFile depends on that.
        return builder.ToImmutable();

        static TextSpan getFullSpan(TextSpan previousWhiteSpaceSpan, SyntaxTrivia trivia)
        {
            // Include the preceding whitespace in the span, i.e., span will be the whole line.
            return previousWhiteSpaceSpan.IsEmpty ? trivia.FullSpan : TextSpan.FromBounds(previousWhiteSpaceSpan.Start, trivia.FullSpan.End);
        }

        static void reportErrorFor(SourceFile sourceFile, SyntaxTrivia trivia)
        {
            if (trivia.ContainsDiagnostics && trivia.IsKind(SyntaxKind.IgnoredDirectiveTrivia))
            {
                throw new GracefulException(CliCommandStrings.CannotConvertDirective, sourceFile.GetLocationString(trivia.Span));
            }
        }
    }
#pragma warning restore RSEXPERIMENTAL003 // 'SyntaxTokenParser' is experimental

    public static SourceFile LoadSourceFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return new SourceFile(filePath, SourceText.From(stream, Encoding.UTF8));
    }

    public static SourceText? RemoveDirectivesFromFile(ImmutableArray<CSharpDirective> directives, SourceText text)
    {
        if (directives.Length == 0)
        {
            return null;
        }

        Debug.Assert(directives.OrderBy(d => d.Span.Start).SequenceEqual(directives), "Directives should be ordered by source location.");

        for (int i = directives.Length - 1; i >= 0; i--)
        {
            var directive = directives[i];
            text = text.Replace(directive.Span, string.Empty);
        }

        return text;
    }

    public static bool IsValidEntryPointPath(string entryPointFilePath)
    {
        return entryPointFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(entryPointFilePath);
    }
}

internal readonly record struct SourceFile(string Path, SourceText Text)
{
    public string GetLocationString(TextSpan span)
    {
        var positionSpan = new FileLinePositionSpan(Path, Text.Lines.GetLinePositionSpan(span));
        return $"{positionSpan.Path}:{positionSpan.StartLinePosition.Line + 1}";
    }

    public FileInfo GetFileInfo() => new FileInfo(Path);
}

/// <summary>
/// C# source file that is in the directory subtree of the current entry point but is not the current entry point.
/// </summary>
internal readonly struct OtherSourceFile
{
    public required string Path { get; init; }

    public SourceText? Text { get; init; }

    /// <summary>
    /// <see langword="false"/> if the file is in a folder nested relative to the current entry point.
    /// </summary>
    public required bool IsTopLevel { get; init; }

    /// <summary>
    /// <see langword="true"/> if this is another entry point that should be excluded.
    /// </summary>
    public required bool IsEntryPoint { get; init; }

    public SourceFile GetOrLoadFile()
    {
        if (Text != null)
        {
            return new SourceFile(Path, Text);
        }

        return VirtualProjectBuildingCommand.LoadSourceFile(Path);
    }
}

internal static partial class Patterns
{
    [GeneratedRegex("""\s+""")]
    public static partial Regex Whitespace { get; }
}

/// <summary>
/// Represents a C# directive starting with <c>#:</c>. Those are ignored by the language but recognized by us.
/// </summary>
internal abstract class CSharpDirective
{
    private CSharpDirective() { }

    /// <summary>
    /// Span of the full line including the trailing line break.
    /// </summary>
    public required TextSpan Span { get; init; }

    public static CSharpDirective Parse(SourceFile sourceFile, TextSpan span, string directiveKind, string directiveText)
    {
        return directiveKind switch
        {
            "sdk" => Sdk.Parse(sourceFile, span, directiveKind, directiveText),
            "property" => Property.Parse(sourceFile, span, directiveKind, directiveText),
            "package" => Package.Parse(sourceFile, span, directiveKind, directiveText),
            _ => throw new GracefulException(CliCommandStrings.UnrecognizedDirective, directiveKind, sourceFile.GetLocationString(span)),
        };
    }

    private static (string, string?) ParseOptionalTwoParts(SourceFile sourceFile, TextSpan span, string directiveKind, string directiveText)
    {
        var i = directiveText.IndexOf(' ', StringComparison.Ordinal);
        var firstPart = checkFirstPart(i < 0 ? directiveText : directiveText[..i]);
        var secondPart = i < 0 ? [] : directiveText.AsSpan((i + 1)..).TrimStart();
        if (i < 0 || secondPart.IsWhiteSpace())
        {
            return (firstPart, null);
        }

        return (firstPart, secondPart.ToString());

        string checkFirstPart(string firstPart)
        {
            if (string.IsNullOrWhiteSpace(firstPart))
            {
                throw new GracefulException(CliCommandStrings.MissingDirectiveName, directiveKind, sourceFile.GetLocationString(span));
            }

            return firstPart;
        }
    }

    /// <summary>
    /// <c>#!</c> directive.
    /// </summary>
    public sealed class Shebang : CSharpDirective;

    /// <summary>
    /// <c>#:sdk</c> directive.
    /// </summary>
    public sealed class Sdk : CSharpDirective
    {
        private Sdk() { }

        public required string Name { get; init; }
        public string? Version { get; init; }

        public static new Sdk Parse(SourceFile sourceFile, TextSpan span, string directiveKind, string directiveText)
        {
            var (sdkName, sdkVersion) = ParseOptionalTwoParts(sourceFile, span, directiveKind, directiveText);

            return new Sdk
            {
                Span = span,
                Name = sdkName,
                Version = sdkVersion,
            };
        }

        public string ToSlashDelimitedString()
        {
            return Version is null ? Name : $"{Name}/{Version}";
        }
    }

    /// <summary>
    /// <c>#:property</c> directive.
    /// </summary>
    public sealed class Property : CSharpDirective
    {
        private Property() { }

        public required string Name { get; init; }
        public required string Value { get; init; }

        public static new Property Parse(SourceFile sourceFile, TextSpan span, string directiveKind, string directiveText)
        {
            var (propertyName, propertyValue) = ParseOptionalTwoParts(sourceFile, span, directiveKind, directiveText);

            if (propertyValue is null)
            {
                throw new GracefulException(CliCommandStrings.PropertyDirectiveMissingParts, sourceFile.GetLocationString(span));
            }

            try
            {
                propertyName = XmlConvert.VerifyName(propertyName);
            }
            catch (XmlException ex)
            {
                throw new GracefulException(string.Format(CliCommandStrings.PropertyDirectiveInvalidName, sourceFile.GetLocationString(span), ex.Message), ex);
            }

            return new Property
            {
                Span = span,
                Name = propertyName,
                Value = propertyValue,
            };
        }
    }

    /// <summary>
    /// <c>#:package</c> directive.
    /// </summary>
    public sealed class Package : CSharpDirective
    {
        private Package() { }

        public required string Name { get; init; }
        public string? Version { get; init; }

        public static new Package Parse(SourceFile sourceFile, TextSpan span, string directiveKind, string directiveText)
        {
            var (packageName, packageVersion) = ParseOptionalTwoParts(sourceFile, span, directiveKind, directiveText);

            return new Package
            {
                Span = span,
                Name = packageName,
                Version = packageVersion,
            };
        }
    }
}

internal sealed class RunFileBuildCacheEntry
{
    private static StringComparer GlobalPropertiesComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// We can't know which parts of the path are case insensitive, so we are conservative
    /// to avoid false positives in the cache (saying we are up to date even if we are not).
    /// </summary>
    private static StringComparer FilePathComparer => StringComparer.Ordinal;

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, string> GlobalProperties { get; }

    /// <summary>
    /// Full paths.
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public HashSet<string> ImplicitBuildFiles { get; }

    /// <summary>
    /// Keys are full paths.
    /// </summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, OtherSource> OtherSources { get; }

    public string? ProjectFileText { get; set; }

    [JsonConstructor]
    public RunFileBuildCacheEntry()
    {
        GlobalProperties = new(GlobalPropertiesComparer);
        ImplicitBuildFiles = new(FilePathComparer);
        OtherSources = new(FilePathComparer);
    }

    public RunFileBuildCacheEntry(Dictionary<string, string> globalProperties)
    {
        Debug.Assert(globalProperties.Comparer == GlobalPropertiesComparer);
        GlobalProperties = globalProperties;
        ImplicitBuildFiles = new(FilePathComparer);
        OtherSources = new(FilePathComparer);
    }

    public readonly record struct OtherSource(bool IsEntryPoint);
}

[JsonSerializable(typeof(RunFileBuildCacheEntry))]
internal partial class RunFileJsonSerializerContext : JsonSerializerContext;
