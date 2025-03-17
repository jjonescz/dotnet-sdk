// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Tools.Run;
using LocalizableStrings = Microsoft.DotNet.Tools.Run.LocalizableStrings;

namespace Microsoft.DotNet.Tools;

/// <summary>
/// Used to build a virtual project file in memory to support <c>dotnet run file.cs</c>.
/// </summary>
internal sealed class VirtualProjectBuildingCommand
{
    private static readonly XmlWriterSettings s_projectFileXmlWriterSettings = new XmlWriterSettings
    {
        Indent = true,
        IndentChars = "  ",
        Encoding = Encoding.UTF8,
        OmitXmlDeclaration = true,
    };

    public Dictionary<string, string> GlobalProperties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public required string EntryPointFileFullPath { get; init; }

    public int Execute(string[] binaryLoggerArgs, ILogger consoleLogger)
    {
        var binaryLogger = GetBinaryLogger(binaryLoggerArgs);
        Dictionary<string, string?> savedEnvironmentVariables = new();
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

            // Do a restore first (equivalent to MSBuild's "implicit restore", i.e., `/restore`).
            // See https://github.com/dotnet/msbuild/blob/a1c2e7402ef0abe36bf493e395b04dd2cb1b3540/src/MSBuild/XMake.cs#L1838
            // and https://github.com/dotnet/msbuild/issues/11519.
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
                if (RunCommand.IsBinLogArgument(arg))
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
    }

    // Kept in sync with the default `dotnet new console` project file (enforced by `DotnetProjectAddTests.SameAsTemplate`).
    private const string CommonProjectProperties = """
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
        """;

    public static void SaveProjectFile(string path, ImmutableArray<CSharpDirective> directives)
    {
        using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        WriteProjectFile(writer, directives, virtualProjectFile: false, targetFilePath: null);
    }

    private static void WriteProjectFile(TextWriter writer, ImmutableArray<CSharpDirective> directives, bool virtualProjectFile, string? targetFilePath)
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

        if (virtualProjectFile)
        {
            writer.WriteLine($"""
                <Project>

                  <!-- We need to explicitly import Sdk props/targets so we can override the targets below. -->
                  <Import Project="Sdk.props" Sdk="{escapeValue(sdkValue)}" />
                """);
        }
        else
        {
            writer.WriteLine($"""
                <Project Sdk="{escapeValue(sdkValue)}">

                """);
        }

        foreach (var sdk in sdkDirectives.Skip(1))
        {
            if (virtualProjectFile)
            {
                writer.WriteLine($"""
                      <Import Project="Sdk.props" Sdk="{escapeValue(sdk.ToSlashDelimitedString())}" />
                    """);
            }
            else if (sdk.Version is null)
            {
                writer.WriteLine($"""
                      <Sdk Name="{escapeValue(sdk.Name)}" />
                    """);
            }
            else
            {
                writer.WriteLine($"""
                      <Sdk Name="{escapeValue(sdk.Name)}" Version="{escapeValue(sdk.Version)}" />
                    """);
            }

            processedDirectives++;
        }

        if (processedDirectives > 1)
        {
            writer.WriteLine();
        }

        writer.WriteLine($"""
              <PropertyGroup>
            {CommonProjectProperties}
              </PropertyGroup>
            """);

        if (virtualProjectFile)
        {
            writer.WriteLine("""

                  <PropertyGroup>
                    <EnableDefaultItems>false</EnableDefaultItems>
                  </PropertyGroup>
                """);
        }

        if (propertyDirectives.Any())
        {
            writer.WriteLine("""

                  <PropertyGroup>
                """);

            foreach (var property in propertyDirectives)
            {
                writer.WriteLine($"""
                        <{escapeName(property.Name)}>{escapeValue(property.Value)}</{escapeName(property.Name)}>
                    """);

                processedDirectives++;
            }

            writer.WriteLine("  </PropertyGroup>");
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
                            <PackageReference Include="{escapeValue(package.Name)}" />
                        """);
                }
                else
                {
                    writer.WriteLine($"""
                            <PackageReference Include="{escapeValue(package.Name)}" Version="{escapeValue(package.Version)}" />
                        """);
                }

                processedDirectives++;
            }

            writer.WriteLine("  </ItemGroup>");
        }

        Debug.Assert(processedDirectives == directives.Length);

        if (virtualProjectFile)
        {
            Debug.Assert(targetFilePath is not null);

            writer.WriteLine($"""

                  <ItemGroup>
                    <Compile Include="{escapeValue(targetFilePath)}" />
                  </ItemGroup>

                """);

            foreach (var sdk in sdkDirectives)
            {
                writer.WriteLine($"""
                      <Import Project="Sdk.targets" Sdk="{escapeValue(sdk.ToSlashDelimitedString())}" />
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

        static string escapeName(string value) => XmlConvert.EncodeName(value);
        static string escapeValue(string value) => SecurityElement.Escape(value);
    }

    private ProjectRootElement CreateProjectRootElement(ProjectCollection projectCollection)
    {
        var sourceFile = CreateSourceFile(EntryPointFileFullPath);
        var directives = FindDirectives(sourceFile);

        // If there were any `#:` directives, remove them from the file.
        // (This is temporary until Roslyn is updated to ignore them.)
        string targetFilePath = EntryPointFileFullPath;
        if (directives.Length != 0)
        {
            var targetDirectory = Path.Join(Path.GetDirectoryName(targetFilePath), "obj");
            Directory.CreateDirectory(targetDirectory);
            targetFilePath = Path.Join(targetDirectory, Path.GetFileName(targetFilePath));

            RemoveDirectivesFromFile(directives, sourceFile.Text, targetFilePath);
        }

        var projectFileFullPath = Path.ChangeExtension(EntryPointFileFullPath, ".csproj");
        var projectFileWriter = new StringWriter();
        WriteProjectFile(projectFileWriter, directives, virtualProjectFile: true, targetFilePath: targetFilePath);
        var projectFileText = projectFileWriter.ToString();

        var projectRoot = CreateProjectRootElement(projectFileText, projectCollection);
        projectRoot.FullPath = projectFileFullPath;
        return projectRoot;
    }

    private static ProjectRootElement CreateProjectRootElement(string text, ProjectCollection projectCollection)
    {
        using var reader = new StringReader(text);
        using var xmlReader = XmlReader.Create(new StringReader(text));
        return ProjectRootElement.Create(xmlReader, projectCollection);
    }

    public static bool IsValidEntryPointPath(string entryPointFilePath)
    {
        return entryPointFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(entryPointFilePath);
    }

    public static ImmutableArray<CSharpDirective> FindDirectives(SourceFile sourceFile)
    {
        var builder = ImmutableArray.CreateBuilder<CSharpDirective>();

        // NOTE: When Roslyn is updated to support "ignored directives", we should use its SyntaxTokenParser instead.
        foreach (var line in sourceFile.Text.Lines)
        {
            if (Patterns.Directive.Match(sourceFile.Text.ToString(line.Span)) is { Success: true } match)
            {
                builder.Add(CSharpDirective.Parse(sourceFile, line.SpanIncludingLineBreak, match.Groups[1].Value, match.Groups[2].Value));
            }
        }

        // The result should be ordered by source location, RemoveDirectivesFromFile depends on that.
        return builder.ToImmutable();
    }

    public static SourceFile CreateSourceFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return new SourceFile(filePath, SourceText.From(stream, Encoding.UTF8));
    }

    public static void RemoveDirectivesFromFile(ImmutableArray<CSharpDirective> directives, SourceText text, string filePath)
    {
        if (directives.Length == 0)
        {
            return;
        }

        Debug.Assert(directives.OrderBy(d => d.Span.Start).SequenceEqual(directives), "Directives should be ordered by source location.");

        for (int i = directives.Length - 1; i >= 0; i--)
        {
            var directive = directives[i];
            text = text.Replace(directive.Span, string.Empty);
        }

        using var stream = File.Open(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        text.Write(writer);
    }
}

internal readonly record struct SourceFile(string Path, SourceText Text)
{
    public FileLinePositionSpan GetPosition(TextSpan span)
    {
        return new FileLinePositionSpan(Path, Text.Lines.GetLinePositionSpan(span));
    }
}

internal static partial class Patterns
{
    [GeneratedRegex("""^\s*#:\s*(\w+)\s*(.*?)\s*$""")]
    public static partial Regex Directive { get; }
}

/// <summary>
/// Represents a C# directive starting with <c>#:</c>. Those are ignored by the language but recognized by us.
/// </summary>
internal abstract record CSharpDirective
{
    private static readonly SearchValues<char> s_separators = SearchValues.Create('/', '=');

    private CSharpDirective() { }

    /// <summary>
    /// Span of the full line including the trailing line break.
    /// </summary>
    public required TextSpan Span { get; init; }

    public static CSharpDirective Parse(SourceFile sourceFile, TextSpan span, string name, string value)
    {
        return name switch
        {
            "sdk" => Sdk.Parse(span, value),
            "property" => Property.Parse(sourceFile, span, value),
            "package" => Package.Parse(span, value),
            _ => throw new GracefulException(LocalizableStrings.UnrecognizedDirective, name, sourceFile.GetPosition(span).ToString()),
        };
    }

    private static (string, string?) ParseNameAndOptionalVersion(string value)
    {
        var i = value.AsSpan().IndexOfAny(s_separators);
        if (i < 0)
        {
            return (value, null);
        }

        return (value[..i], value[(i + 1)..]);
    }

    /// <summary>
    /// <c>#:sdk</c> directive.
    /// </summary>
    public sealed record Sdk : CSharpDirective
    {
        private Sdk() { }

        public required string Name { get; init; }
        public string? Version { get; init; }

        public static Sdk Parse(TextSpan span, string value)
        {
            var (name, version) = ParseNameAndOptionalVersion(value);

            return new Sdk
            {
                Span = span,
                Name = name,
                Version = version,
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
    public sealed record Property : CSharpDirective
    {
        private Property() { }

        public required string Name { get; init; }
        public required string Value { get; init; }

        public static Property Parse(SourceFile sourceFile, TextSpan span, string text)
        {
            var (name, value) = ParseNameAndOptionalVersion(text);

            if (value is null)
            {
                throw new GracefulException(LocalizableStrings.PropertyDirectiveMissingParts, sourceFile.GetPosition(span).ToString());
            }

            return new Property
            {
                Span = span,
                Name = name,
                Value = value,
            };
        }
    }

    /// <summary>
    /// <c>#:package</c> directive.
    /// </summary>
    public sealed record Package : CSharpDirective
    {
        private Package() { }

        public required string Name { get; init; }
        public string? Version { get; init; }

        public static Package Parse(TextSpan span, string value)
        {
            var (name, version) = ParseNameAndOptionalVersion(value);

            return new Package
            {
                Span = span,
                Name = name,
                Version = version,
            };
        }
    }
}
