// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.CommandLine;
using Microsoft.TemplateEngine.Cli.Commands;

namespace Microsoft.DotNet.Cli.Commands.Project.Convert;

internal sealed class ProjectConvertCommandParser
{
    public static readonly CliArgument<string> FileOrDirectoryArgument = new(CliCommandStrings.FileOrDirectoryArgumentName)
    {
        Description = CliCommandStrings.FileOrDirectoryArgumentDescription,
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static readonly CliOption<bool> ForceOption = new("--force")
    {
        Description = CliCommandStrings.CmdOptionForceDescription,
        Arity = ArgumentArity.Zero,
    };

    public static readonly CliOption<string> SharedDirectoryNameOption = new("--shared-directory-name")
    {
        Description = CliCommandStrings.CmdOptionSharedDirectoryNameDescription,
        Arity = ArgumentArity.ExactlyOne,
        DefaultValueFactory = _ => "Shared",
    };

    public static CliCommand GetCommand()
    {
        CliCommand command = new("convert", CliCommandStrings.ProjectConvertAppFullName)
        {
            FileOrDirectoryArgument,
            SharedOptions.OutputOption,
            ForceOption,
            SharedDirectoryNameOption,
        };

        command.SetAction((parseResult) => new ProjectConvertCommand(parseResult).Execute());
        return command;
    }
}
