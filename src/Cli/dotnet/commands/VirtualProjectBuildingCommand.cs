// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.DotNet.Tools;

internal sealed class VirtualProjectBuildingCommand
{
    public required string EntryPointFileFullPath { get; init; }

    public int Execute(out Func<ProjectCollection, ProjectInstance>? projectFactory)
    {
        // Setup MSBuild.
        var binaryLogger = new BinaryLogger
        {
            Parameters = "msbuild.binlog",
            CollectProjectImports = BinaryLogger.ProjectImportsCollectionMode.Embed,
        };
        var consoleLogger = new ConsoleLogger(LoggerVerbosity.Quiet);
        try
        {
            IEnumerable<ILogger> loggers = [binaryLogger, consoleLogger];
            var globalProperties = MSBuildForwardingAppWithoutLogging.GetMSBuildRequiredEnvironmentVariables();
            var parameters = new BuildParameters
            {
                GlobalProperties = globalProperties,
                Loggers = loggers,
                LogTaskInputs = true,
                LogInitialPropertiesAndItems = true,
            };
            BuildManager.DefaultBuildManager.BeginBuild(parameters);

            // Create a virtual project file.
            var projectFileFullPath = Path.ChangeExtension(EntryPointFileFullPath, ".csproj");
            var projectFileText = """
                <Project>
                    <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />

                    <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>net9.0</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                    </PropertyGroup>

                    <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />

                    <!-- Override targets which don't work with project files that are not present on disk. -->

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
                </Project>
                """;
            projectFactory = (projectCollection) =>
            {
                ProjectRootElement projectRoot;
                using (var xmlReader = XmlReader.Create(new StringReader(projectFileText)))
                {
                    projectRoot = ProjectRootElement.Create(xmlReader, projectCollection);
                }
                projectRoot.FullPath = projectFileFullPath;
                return ProjectInstance.FromProjectRootElement(projectRoot, new ProjectOptions());
            };
            ProjectRootElement projectRoot;
            using (var xmlReader = XmlReader.Create(new StringReader(projectFileText)))
            {
                projectRoot = ProjectRootElement.Create(xmlReader);
            }
            projectRoot.FullPath = projectFileFullPath;

            // Do a restore first (equivalent to MSBuild's "implicit restore", i.e., `/restore`).
            // See https://github.com/dotnet/msbuild/blob/a1c2e7402ef0abe36bf493e395b04dd2cb1b3540/src/MSBuild/XMake.cs#L1838.
            var restoreRequest = new BuildRequestData(
                ProjectInstance.FromProjectRootElement(projectRoot, new ProjectOptions
                {
                    GlobalProperties = new Dictionary<string, string>()
                    {
                        ["MSBuildRestoreSessionId"] = Guid.NewGuid().ToString("D"),
                        ["MSBuildIsRestoring"] = bool.TrueString,
                    },
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
                ProjectInstance.FromProjectRootElement(projectRoot, new ProjectOptions()),
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
            projectFactory = null;
            return 1;
        }
        finally
        {
            binaryLogger.Shutdown();
            consoleLogger.Shutdown();
        }
    }
}
