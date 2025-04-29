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
            parseDirectivesFromOtherEntryPoints: true,
            reportAllDirectiveErrors: !_force,
            otherEntryPoints: out var otherEntryPoints,
            parsedFiles: out var parsedFiles);

        // If there are other entry points, a directory must be specified (so it's clear that we convert all the entry points, not just the specified one).
        if (isFile && otherEntryPoints.Length != 0)
        {
            throw new GracefulException(CliCommandStrings.DirectoryMustBeSpecified, fileOrDirectory);
        }

        ReadOnlySpan<string> currentEntryPoint = entryPointSourceFile is { } file ? [file.Path] : [];
        ReadOnlySpan<string> allEntryPoints = [.. currentEntryPoint, .. otherEntryPoints];

        // Check there are some entry points.
        if (allEntryPoints.Length == 0)
        {
            throw new GracefulException(CliCommandStrings.NoEntryPoints, fileOrDirectory);
        }

        // Determine the base target directory.
        string baseTargetDirectory;
        if (_outputDirectory != null)
        {
            baseTargetDirectory = _outputDirectory;
            Directory.CreateDirectory(baseTargetDirectory);
        }
        else
        {
            baseTargetDirectory = Environment.CurrentDirectory;
        }

        string? sharedDirectory = null;
        bool deleteSharedSourceFiles = false;

        // Process files.
        foreach (var parsed in parsedFiles.Values)
        {
            string targetDirectory;
            bool deleteSourceFiles;

            if (parsed.IsEntryPoint)
            {
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(parsed.File.Path);

                // If there is a single entry point, generate the project directly in the output folder, otherwise create a subfolder.
                if (allEntryPoints.Length > 1)
                {
                    targetDirectory = Path.Join(baseTargetDirectory, fileNameWithoutExtension);
                    Directory.CreateDirectory(targetDirectory);
                    deleteSourceFiles = true;
                }
                else
                {
                    targetDirectory = baseTargetDirectory;
                    deleteSourceFiles = _outputDirectory != null;
                }

                // Generate a project file.
                string projectFile = Path.Join(targetDirectory, fileNameWithoutExtension + ".csproj");
                using (var csprojStream = File.Open(projectFile, FileMode.Create, FileAccess.Write))
                using (var csprojWriter = new StreamWriter(csprojStream, Encoding.UTF8))
                {
                    VirtualProjectBuildingCommand.WriteProjectFile(csprojWriter, parsed.SortedDirectives, isVirtualProject: false);
                }
            }
            else
            {
                if (sharedDirectory == null)
                {
                    // If there are multiple entry points, we need a Shared folder
                    // (or SharedX where X is a unique number to avoid conflicts).
                    if (allEntryPoints.Length > 1)
                    {
                        sharedDirectory = Path.Join(baseTargetDirectory, "Shared");
                        Directory.CreateDirectory(sharedDirectory);
                        deleteSharedSourceFiles = true;
                    }
                    else
                    {
                        sharedDirectory = baseTargetDirectory;
                        deleteSharedSourceFiles = _outputDirectory != null;
                    }
                }

                targetDirectory = sharedDirectory;
                deleteSourceFiles = deleteSharedSourceFiles;
            }

            // Remove directives. Write the converted file or move it if no conversion is needed.
            var targetFile = Path.Join(targetDirectory, Path.GetFileName(parsed.File.Path));
            if (VirtualProjectBuildingCommand.RemoveDirectivesFromFile(parsed.Directives, parsed.File.Text) is { } convertedEntryPointFileText)
            {
                using var stream = File.Open(targetFile, FileMode.Create, FileAccess.Write);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                convertedEntryPointFileText.Write(writer);

                if (deleteSourceFiles)
                {
                    File.Delete(parsed.File.Path);
                }
            }
            else if (deleteSourceFiles)
            {
                File.Move(parsed.File.Path, targetFile);
            }
        }

        return 0;
    }
}
