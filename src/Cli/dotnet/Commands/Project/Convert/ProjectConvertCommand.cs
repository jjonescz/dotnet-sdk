// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.CommandLine;
using Microsoft.DotNet.Cli.Commands.Run;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.TemplateEngine.Cli.Commands;

namespace Microsoft.DotNet.Cli.Commands.Project.Convert;

internal sealed class ProjectConvertCommand(ParseResult parseResult) : CommandBase(parseResult)
{
    private readonly string? _fileOrDirectory = parseResult.GetValue(ProjectConvertCommandParser.FileOrDirectoryArgument);
    private readonly string? _outputDirectory = parseResult.GetValue(SharedOptions.OutputOption)?.FullName;
    private readonly bool _force = parseResult.GetValue(ProjectConvertCommandParser.ForceOption);

    public override int Execute()
    {
        // Check target directory.
        if (_outputDirectory != null && Directory.Exists(_outputDirectory))
        {
            throw new GracefulException(CliCommandStrings.DirectoryAlreadyExists, _outputDirectory);
        }

        // Check entry-point file path.
        string fileOrDirectory = Path.GetFullPath(_fileOrDirectory!);
        bool isFile = VirtualProjectBuildingCommand.IsValidEntryPointPath(fileOrDirectory);
        if (!isFile && (File.Exists(fileOrDirectory) || !Directory.Exists(fileOrDirectory)))
        {
            throw new GracefulException(CliCommandStrings.InvalidFileOrDirectoryPath, fileOrDirectory);
        }

        // Discover other files.
        SourceFile? entryPointSourceFile = isFile ? VirtualProjectBuildingCommand.LoadSourceFile(fileOrDirectory) : null;
        VirtualProjectBuildingCommand.DiscoverOtherFiles(
            entryPointFile: entryPointSourceFile,
            entryDirectory: isFile ? null : new DirectoryInfo(fileOrDirectory),
            reportAllDirectiveErrors: !_force,
            otherEntryPoints: out var otherEntryPoints,
            allFiles: out var allFiles,
            sortedDirectives: out var sortedDirectives);

        // If there are other entry points, a directory must be specified (so it's clear that we convert all the entry points, not just the specified one).
        if (isFile && otherEntryPoints.Length != 0)
        {
            throw new GracefulException(CliCommandStrings.DirectoryMustBeSpecified, fileOrDirectory);
        }

        ReadOnlySpan<SourceFile> currentEntryPoint = entryPointSourceFile is { } file ? [file] : [];
        ReadOnlySpan<SourceFile> allEntryPoints = [.. currentEntryPoint, .. otherEntryPoints];

        // Check there are some entry points.
        if (allEntryPoints.Length == 0)
        {
            throw new GracefulException(CliCommandStrings.NoEntryPoints, fileOrDirectory);
        }

        // If there is a single entry point, generate the project directly in the output folder, otherwise create a subfolder.
        string targetDirectory = _outputDirectory ?? Environment.CurrentDirectory;
        bool deleteSourceFiles = _outputDirectory != null;
        if (allEntryPoints.Length > 1)
        {
            targetDirectory = Path.Join(targetDirectory, Path.GetFileNameWithoutExtension(fileOrDirectory));
            deleteSourceFiles = true;
        }

        // Generate project file per entry point.
        foreach (var entryPoint in allEntryPoints)
        {
            Directory.CreateDirectory(targetDirectory);
            string projectFile = Path.Join(targetDirectory, Path.GetFileNameWithoutExtension(entryPoint.Path) + ".csproj");
            using (var csprojStream = File.Open(projectFile, FileMode.Create, FileAccess.Write))
            using (var csprojWriter = new StreamWriter(csprojStream, Encoding.UTF8))
            {
                VirtualProjectBuildingCommand.WriteProjectFile(csprojWriter, sortedDirectives, isVirtualProject: false);
            }
        }

        // Remove directives from files.
        foreach (var info in allFiles.Values)
        {
            var targetFile = Path.Join(targetDirectory, Path.GetFileName(info.File.Path));

            // Write the converted file or move it if no conversion is needed.
            if (VirtualProjectBuildingCommand.RemoveDirectivesFromFile(info.Directives, info.File.Text) is { } convertedEntryPointFileText)
            {
                using var stream = File.Open(targetFile, FileMode.Create, FileAccess.Write);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                convertedEntryPointFileText.Write(writer);

                if (deleteSourceFiles)
                {
                    File.Delete(info.File.Path);
                }
            }
            else if (deleteSourceFiles)
            {
                File.Move(info.File.Path, targetFile);
            }
        }

        return 0;
    }
}
