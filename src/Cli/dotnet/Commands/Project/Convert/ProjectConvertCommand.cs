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
    private readonly string _file = parseResult.GetValue(ProjectConvertCommandParser.FileArgument) ?? string.Empty;
    private readonly string? _outputDirectory = parseResult.GetValue(SharedOptions.OutputOption)?.FullName;
    private readonly bool _force = parseResult.GetValue(ProjectConvertCommandParser.ForceOption);

    public override int Execute()
    {
        string file = Path.GetFullPath(_file);
        if (!VirtualProjectBuildingCommand.IsValidEntryPointPath(file))
        {
            throw new GracefulException(CliCommandStrings.InvalidFilePath, file);
        }

        string targetDirectory = _outputDirectory ?? Path.ChangeExtension(file, null);
        if (Directory.Exists(targetDirectory))
        {
            throw new GracefulException(CliCommandStrings.DirectoryAlreadyExists, targetDirectory);
        }

        // Generate project file.
        // TODO: Load directives from other files.
        SourceFile sourceFile = VirtualProjectBuildingCommand.LoadSourceFile(file);
        var directives = VirtualProjectBuildingCommand.FindDirectivesForConversion(sourceFile, force: _force);

        Directory.CreateDirectory(targetDirectory);
        string projectFile = Path.Join(targetDirectory, Path.GetFileNameWithoutExtension(file) + ".csproj");
        using (var csprojStream = File.Open(projectFile, FileMode.Create, FileAccess.Write))
        using (var csprojWriter = new StreamWriter(csprojStream, Encoding.UTF8))
        {
            VirtualProjectBuildingCommand.WriteConvertedProjectFile(csprojWriter, directives);
        }

        var targetFile = Path.Join(targetDirectory, Path.GetFileName(file));

        // Write the converted entry point file or move it if no conversion is needed.
        if (VirtualProjectBuildingCommand.RemoveDirectivesFromFile(directives, sourceFile.Text) is { } convertedEntryPointFileText)
        {
            using var stream = File.Open(targetFile, FileMode.Create, FileAccess.Write);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            convertedEntryPointFileText.Write(writer);
            File.Delete(file);
        }
        else
        {
            File.Move(file, targetFile);
        }

        return 0;
    }
}
