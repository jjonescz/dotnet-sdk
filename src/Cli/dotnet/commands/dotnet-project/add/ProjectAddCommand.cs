// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.CommandLine;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.DotNet.Tools.Project.Add;

internal sealed class ProjectAddCommand : CommandBase
{
    private readonly string _directory;

    public ProjectAddCommand(ParseResult parseResult) : base(parseResult)
    {
        _directory = parseResult.GetValue(ProjectAddCommandParser.DirectoryOption) ?? Environment.CurrentDirectory;
    }

    public override int Execute()
    {
        string? existingProjectFile = Directory.EnumerateFiles(_directory, "*.*proj").FirstOrDefault();
        if (existingProjectFile is not null)
        {
            throw new GracefulException(LocalizableStrings.ProjectFileAlreadyExists, existingProjectFile);
        }

        string entryPointFile = FindSingleEntryPointFile(_directory);
        string projectFilePath = Path.ChangeExtension(entryPointFile, ".csproj");
        string projectFileText = VirtualProjectBuildingCommand.GetNonVirtualProjectFileText();
        File.WriteAllText(path: projectFilePath, contents: projectFileText);
        return 0;
    }

    static string FindSingleEntryPointFile(string directory)
    {
        string? candidate = null;
        foreach (string file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            if (VirtualProjectBuildingCommand.HasTopLevelStatements(file))
            {
                if (candidate is not null)
                {
                    throw new GracefulException(LocalizableStrings.MultipleEntryPointFiles,
                        Path.GetFileName(candidate), Path.GetFileName(file), directory);
                }

                candidate = file;
            }
        }

        if (candidate is null)
        {
            throw new GracefulException(LocalizableStrings.NoEntryPointFile, directory);
        }

        return candidate;
    }
}
