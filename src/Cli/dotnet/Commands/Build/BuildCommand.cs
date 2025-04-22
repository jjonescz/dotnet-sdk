// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Microsoft.Build.Logging;
using Microsoft.DotNet.Cli.Commands.Restore;
using Microsoft.DotNet.Cli.Commands.Run;
using Microsoft.DotNet.Cli.Extensions;

namespace Microsoft.DotNet.Cli.Commands.Build;

public class BuildCommand(
    IEnumerable<string> msbuildArgs,
    bool noRestore,
    string msbuildPath = null) : RestoringCommand(msbuildArgs, noRestore, msbuildPath)
{
    public string? FileBasedProgramPath { get; init; }

    public static BuildCommand FromArgs(string[] args, string msbuildPath = null)
    {
        var parser = Parser.Instance;
        var parseResult = parser.ParseFrom("dotnet build", args);
        return FromParseResult(parseResult, msbuildPath);
    }

    public static BuildCommand FromParseResult(ParseResult parseResult, string msbuildPath = null)
    {
        PerformanceLogEventSource.Log.CreateBuildCommandStart();

        var msbuildArgs = new List<string>();

        parseResult.ShowHelpOrErrorIfAppropriate();

        CommonOptions.ValidateSelfContainedOptions(
            parseResult.GetResult(BuildCommandParser.SelfContainedOption) is not null,
            parseResult.GetResult(BuildCommandParser.NoSelfContainedOption) is not null);

        msbuildArgs.Add($"-consoleloggerparameters:Summary");

        if (parseResult.GetResult(BuildCommandParser.NoIncrementalOption) is not null)
        {
            msbuildArgs.Add("-target:Rebuild");
        }

        msbuildArgs.AddRange(parseResult.OptionValuesToBeForwarded(BuildCommandParser.GetCommand()));

        var fileArgument = parseResult.GetValue(BuildCommandParser.SlnOrProjectOrFileArgument);

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

        bool noRestore = parseResult.GetResult(BuildCommandParser.NoRestoreOption) is not null;

        BuildCommand command = new(
            msbuildArgs,
            noRestore,
            msbuildPath)
        {
            FileBasedProgramPath = fileBasedProgramPath,
        };

        PerformanceLogEventSource.Log.CreateBuildCommandStop();

        return command;
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
            noRestore: false, // TODO
            noCache: true,
            noBuild: false);
    }
}
