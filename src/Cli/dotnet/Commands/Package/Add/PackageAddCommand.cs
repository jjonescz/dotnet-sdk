// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Microsoft.DotNet.Cli.Commands.MSBuild;
using Microsoft.DotNet.Cli.Commands.NuGet;
using Microsoft.DotNet.Cli.Commands.Run;
using Microsoft.DotNet.Cli.Extensions;
using Microsoft.DotNet.Cli.Utils;
using NuGet.Packaging.Core;

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

    private int ExecuteForFileBasedApp()
    {
        // Check disallowed options.
        ReadOnlySpan<Option> disallowedOptions =
        [
            PackageAddCommandParser.FrameworkOption,
                PackageAddCommandParser.NoRestoreOption,
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

        // Perform the edit.
        var editor = FileBasedAppSourceEditor.Load(SourceFile.Load(Path.GetFullPath(fileOrDirectory)));
        editor.Add(new CSharpDirective.Package { Span = default, Name = _packageId.Id, Version = _packageId.HasVersion ? _packageId.Version.ToString() : null });
        editor.SourceFile.Save();
        return 0;
    }
}
