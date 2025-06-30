// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using Microsoft.Build.Evaluation;
using Microsoft.DotNet.Cli.Commands.MSBuild;
using Microsoft.DotNet.Cli.Commands.NuGet;
using Microsoft.DotNet.Cli.Commands.Run;
using Microsoft.DotNet.Cli.Extensions;
using Microsoft.DotNet.Cli.Utils;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;

namespace Microsoft.DotNet.Cli.Commands.Package.Add;

/// <param name="fileOrDirectory">
/// Since this command is invoked via both 'package add' and 'add package', different symbols will control what the project path to search is. 
/// It's cleaner for the separate callsites to know this instead of pushing that logic here.
/// </param>
internal class PackageAddCommand(ParseResult parseResult, string fileOrDirectory, AppKinds allowedAppKinds) : CommandBase(parseResult)
{
    private readonly PackageIdentity _packageId = parseResult.GetValue(PackageAddCommandParser.CmdPackageArgument)!;

    public override int Execute()
    {
        if (allowedAppKinds.HasFlag(AppKinds.FileBased) && VirtualProjectBuildingCommand.IsValidEntryPointPath(fileOrDirectory))
        {
            return ExecuteForFileBasedApp();
        }

        Debug.Assert(allowedAppKinds.HasFlag(AppKinds.ProjectBased));

        string projectFilePath;
        if (!File.Exists(fileOrDirectory))
        {
            projectFilePath = MsbuildProject.GetProjectFileFromDirectory(fileOrDirectory).FullName;
        }
        else
        {
            projectFilePath = fileOrDirectory;
        }

        var tempDgFilePath = string.Empty;

        if (_parseResult.GetResult(PackageAddCommandParser.NoRestoreOption) is null)
        {

            try
            {
                // Create a Dependency Graph file for the project
                tempDgFilePath = Path.GetTempFileName();
            }
            catch (IOException ioex)
            {
                // Catch IOException from Path.GetTempFileName() and throw a graceful exception to the user.
                throw new GracefulException(string.Format(CliCommandStrings.CmdDGFileIOException, projectFilePath), ioex);
            }

            GetProjectDependencyGraph(projectFilePath, tempDgFilePath);
        }

        var result = NuGetCommand.Run(
            TransformArgs(
                _packageId,
                tempDgFilePath,
                projectFilePath));
        DisposeTemporaryFile(tempDgFilePath);

        return result;
    }

    private static void GetProjectDependencyGraph(string projectFilePath, string dgFilePath)
    {
        List<string> args =
        [
            // Pass the project file path
            projectFilePath,

            // Pass the task as generate restore Dependency Graph file
            "-target:GenerateRestoreGraphFile",

            // Pass Dependency Graph file output path
            $"-property:RestoreGraphOutputPath=\"{dgFilePath}\"",

            // Turn off recursive restore
            $"-property:RestoreRecursive=false",

            // Turn off restore for Dotnet cli tool references so that we do not generate extra dg specs
            $"-property:RestoreDotnetCliToolReferences=false",

            // Output should not include MSBuild version header
            "-nologo"
        ];

        var result = new MSBuildForwardingApp(args).Execute();

        if (result != 0)
        {
            throw new GracefulException(string.Format(CliCommandStrings.CmdDGFileException, projectFilePath));
        }
    }

    private static void DisposeTemporaryFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private string[] TransformArgs(PackageIdentity packageId, string tempDgFilePath, string projectFilePath)
    {
        List<string> args = [
            "package",
            "add",
            "--package",
            packageId.Id,
            "--project",
            projectFilePath
        ];

        if (packageId.HasVersion)
        {
            args.Add("--version");
            args.Add(packageId.Version.ToString());
        }

        args.AddRange(_parseResult
            .OptionValuesToBeForwarded(PackageAddCommandParser.GetCommand())
            .SelectMany(a => a.Split(' ', 2)));

        if (_parseResult.GetResult(PackageAddCommandParser.NoRestoreOption) is not null)
        {
            args.Add("--no-restore");
        }
        else
        {
            args.Add("--dg-file");
            args.Add(tempDgFilePath);
        }

        return [.. args];
    }

    // More logic should live in NuGet: https://github.com/NuGet/Home/issues/14390
    private int ExecuteForFileBasedApp()
    {
        // Check disallowed options.
        ReadOnlySpan<Option> disallowedOptions =
        [
            PackageAddCommandParser.FrameworkOption,
            PackageAddCommandParser.SourceOption,
            PackageAddCommandParser.PackageDirOption,
        ];
        foreach (var option in disallowedOptions)
        {
            if (_parseResult.HasOption(option))
            {
                throw new GracefulException(CliCommandStrings.InvalidOptionForFileBasedApp, option.Name);
            }
        }

        bool hasVersion = _packageId.HasVersion;
        bool prerelease = _parseResult.GetValue(PackageAddCommandParser.PrereleaseOption);

        if (hasVersion && prerelease)
        {
            throw new GracefulException(CliCommandStrings.PrereleaseAndVersionAreNotSupportedAtTheSameTime);
        }

        var fullPath = Path.GetFullPath(fileOrDirectory);

        // Create restore command, used also for obtaining MSBuild properties.
        bool interactive = _parseResult.GetValue(PackageAddCommandParser.InteractiveOption);
        var command = new VirtualProjectBuildingCommand(
            entryPointFileFullPath: fullPath,
            msbuildArgs: [$"-property:NuGetInteractive={(interactive ? "true" : "false")}"])
        {
            NoCache = true,
            NoBuild = true,
        };
        var projectCollection = new ProjectCollection();
        var projectInstance = command.CreateProjectInstance(projectCollection);

        // Set initial version to Directory.Packages.props or C# file
        // (we always need to add the package reference to the C# file but when CPM is enabled, it's added without a version).
        string version = hasVersion
            ? _packageId.Version.ToString()
            : prerelease
            ? "*-*"
            : "*";
        var cpm = SetCpmVersion(version);
        var nonCpm = SetNonCpmVersion(cpm != null ? null : version);

        if (!_parseResult.GetValue(PackageAddCommandParser.NoRestoreOption))
        {
            // Restore.
            int exitCode = command.Execute();
            if (exitCode != 0)
            {
                // If restore fails, revert any changes made.
                cpm?.Revert();
                return exitCode;
            }

            // If no version was specified by the user, save the actually restored version.
            if (!hasVersion)
            {
                var projectAssetsFile = projectInstance.GetProperty("ProjectAssetsFile")?.EvaluatedValue;
                if (!File.Exists(projectAssetsFile))
                {
                    Reporter.Verbose.WriteLine($"Assets file does not exist: {projectAssetsFile}");
                }
                else
                {
                    var lockFile = new LockFileFormat().Read(projectAssetsFile);
                    var library = lockFile.Libraries.FirstOrDefault(l => string.Equals(l.Name, _packageId.Id, StringComparison.OrdinalIgnoreCase));
                    if (library != null)
                    {
                        var restoredVersion = library.Version.ToString();
                        if (cpm is { } cpmValue)
                        {
                            cpmValue.Update(restoredVersion);
                        }
                        else
                        {
                            nonCpm.Update(restoredVersion);
                        }

                        return 0;
                    }
                }
            }
        }

        nonCpm.Save();
        return 0;

        (Action Save, Action<string> Update) SetNonCpmVersion(string? version)
        {
            // Add #:package directive to the C# file.
            var file = SourceFile.Load(fullPath);
            var editor = FileBasedAppSourceEditor.Load(file);
            editor.Add(new CSharpDirective.Package { Span = default, Name = _packageId.Id, Version = version });
            command.Directives = editor.Directives;
            return (Save, Update);

            void Save()
            {
                editor.SourceFile.Save();
            }

            void Update(string value)
            {
                // Update the C# file with the given version.
                editor.Add(new CSharpDirective.Package { Span = default, Name = _packageId.Id, Version = value });
                editor.SourceFile.Save();
            }
        }

        (Action Revert, Action<string> Update)? SetCpmVersion(string version)
        {
            // Find out whether CPM is enabled.
            if (!string.Equals(projectInstance.GetProperty("ManagePackageVersionsCentrally")?.EvaluatedValue, bool.TrueString, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Load the Directory.Packages.props project.
            var directoryPackagesPropsPath = projectInstance.GetProperty("DirectoryPackagesPropsPath")?.EvaluatedValue;
            if (!File.Exists(directoryPackagesPropsPath))
            {
                Reporter.Verbose.WriteLine($"Directory.Packages.props file does not exist: {directoryPackagesPropsPath}");
                return null;
            }

            var directoryPackagesPropsProject = projectCollection.LoadProject(directoryPackagesPropsPath);
            var snapshot = directoryPackagesPropsProject.Xml.DeepClone();

            const string packageVersionItemType = "PackageVersion";
            const string versionAttributeName = "Version";

            // Update existing PackageVersion if it exists.
            var packageVersion = directoryPackagesPropsProject.GetItems(packageVersionItemType)
                .LastOrDefault(i => string.Equals(i.EvaluatedInclude, _packageId.Id, StringComparison.OrdinalIgnoreCase));
            if (packageVersion != null)
            {
                var packageVersionItemElement = packageVersion.Project.GetItemProvenance(packageVersion).LastOrDefault()?.ItemElement;
                var versionAttribute = packageVersionItemElement?.Metadata.FirstOrDefault(i => i.Name.Equals(versionAttributeName, StringComparison.OrdinalIgnoreCase));
                if (versionAttribute != null)
                {
                    versionAttribute.Value = version;
                    directoryPackagesPropsProject.Save();

                    return (Revert, Update);

                    void Update(string value)
                    {
                        versionAttribute.Value = value;
                        directoryPackagesPropsProject.Save();
                    }
                }
            }

            {
                // Get the ItemGroup to add a PackageVersion to or create a new one.
                var itemGroup = directoryPackagesPropsProject.Xml.ItemGroups
                        .Where(e => e.Items.Any(i => string.Equals(i.ItemType, packageVersionItemType, StringComparison.OrdinalIgnoreCase)))
                        .FirstOrDefault()
                    ?? directoryPackagesPropsProject.Xml.AddItemGroup();

                // Add a PackageVersion item.
                var item = itemGroup.AddItem(packageVersionItemType, _packageId.Id);
                var metadata = item.AddMetadata(versionAttributeName, version, expressAsAttribute: true);
                directoryPackagesPropsProject.Save();

                return (Revert, Update);

                void Update(string value)
                {
                    metadata.Value = value;
                    directoryPackagesPropsProject.Save();
                }
            }

            void Revert()
            {
                snapshot.Save(path: directoryPackagesPropsPath);
            }
        }
    }
}
