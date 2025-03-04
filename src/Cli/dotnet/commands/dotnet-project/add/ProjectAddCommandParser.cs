// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.CommandLine;
using Microsoft.DotNet.Tools.Project.Add;
using LocalizableStrings = Microsoft.DotNet.Tools.Project.Add.LocalizableStrings;

namespace Microsoft.DotNet.Cli;

internal sealed class ProjectAddCommandParser
{
    public static readonly CliOption<string> DirectoryOption = new("--directory")
    {
        Description = LocalizableStrings.CmdDirectoryDescription,
        HelpName = LocalizableStrings.CmdDirectoryPathName,
        Arity = ArgumentArity.ExactlyOne
    };

    public static CliCommand GetCommand()
    {
        CliCommand command = new("add", LocalizableStrings.AppFullName);
        command.Options.Add(DirectoryOption);

        command.SetAction((parseResult) => new ProjectAddCommand(parseResult).Execute());
        return command;
    }
}
