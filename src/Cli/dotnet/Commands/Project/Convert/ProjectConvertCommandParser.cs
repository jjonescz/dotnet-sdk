// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.CommandLine;
using Microsoft.TemplateEngine.Cli.Commands;

namespace Microsoft.DotNet.Cli.Commands.Project.Convert;

internal sealed class ProjectConvertCommandParser
{
    public static readonly Argument<string> FileArgument = new("file")
    {
        Description = CliCommandStrings.CmdFileDescription,
        Arity = ArgumentArity.ExactlyOne,
    };

    public static readonly Option<bool> ForceOption = new("--force")
    {
        Description = CliCommandStrings.CmdOptionForceDescription,
        Arity = ArgumentArity.Zero,
    };

    /// <summary>
    /// API mode. Takes input as JSON, produces JSON, doesn't perform any changes. Used by IDE to see the project file behind a file-based program.
    /// </summary>
    public static readonly Option<bool> ApiOption = new("--api")
    {
        Hidden = true,
        Arity = ArgumentArity.Zero,
    };

    public static Command GetCommand()
    {
        Command command = new("convert", CliCommandStrings.ProjectConvertAppFullName)
        {
            FileArgument,
            SharedOptions.OutputOption,
            ForceOption,
            ApiOption,
        };

        command.SetAction((parseResult) => new ProjectConvertCommand(parseResult).Execute());
        return command;
    }
}
