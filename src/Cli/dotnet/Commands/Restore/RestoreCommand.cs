// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Microsoft.Build.Logging;
using Microsoft.DotNet.Cli.Commands.MSBuild;
using Microsoft.DotNet.Cli.Commands.Run;
using Microsoft.DotNet.Cli.Extensions;
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.DotNet.Cli.Commands.Restore;

public class RestoreCommand : MSBuildForwardingApp
{
    public RestoreCommand(IEnumerable<string> msbuildArgs, string msbuildPath = null)
        : base(msbuildArgs, msbuildPath)
    {
        NuGetSignatureVerificationEnabler.ConditionallyEnable(this);
    }

    public string? FileBasedProgramPath { get; init; }

    public static RestoreCommand FromArgs(string[] args, string msbuildPath = null)
    {
        var parser = Parser.Instance;
        var result = parser.ParseFrom("dotnet restore", args);
        return FromParseResult(result, msbuildPath);
    }

    public static RestoreCommand FromParseResult(ParseResult result, string msbuildPath = null)
    {
        result.HandleDebugSwitch();

        result.ShowHelpOrErrorIfAppropriate();

        List<string> msbuildArgs = ["-target:Restore"];

        msbuildArgs.AddRange(result.OptionValuesToBeForwarded(RestoreCommandParser.GetCommand()));

        var fileArgument = result.GetValue(RestoreCommandParser.SlnOrProjectOrFileArgument);

        string? fileBasedProgramPath;

        if (fileArgument is [{ } arg] && VirtualProjectBuildingCommand.IsValidEntryPointPath(arg))
        {
            fileBasedProgramPath = Path.GetFullPath(arg);
        }
        else
        {
            fileBasedProgramPath = null;
            msbuildArgs.AddRange(fileArgument ?? []);
        }

        return new RestoreCommand(msbuildArgs, msbuildPath)
        {
            FileBasedProgramPath = fileBasedProgramPath,
        };
    }

    public static int Run(string[] args)
    {
        DebugHelper.HandleDebugSwitch(ref args);

        return FromArgs(args).Execute();
    }

    public static int Run(ParseResult parseResult)
    {
        parseResult.HandleDebugSwitch();

        return FromParseResult(parseResult).Execute();
    }

    public override int Execute()
    {
        if (FileBasedProgramPath is null)
        {
            return base.Execute();
        }

        var command = new VirtualProjectBuildingCommand
        {
            EntryPointFileFullPath = FileBasedProgramPath,
        };
        return command.Execute(
            binaryLoggerArgs: [], // TODO
            new ConsoleLogger(), // TODO
            noRestore: false,
            noCache: true,
            noBuild: true);
    }
}
