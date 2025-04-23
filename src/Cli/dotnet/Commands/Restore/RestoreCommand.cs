// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Microsoft.Build.Framework;
using Microsoft.DotNet.Cli.Commands.MSBuild;
using Microsoft.DotNet.Cli.Commands.Run;
using Microsoft.DotNet.Cli.Extensions;
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.DotNet.Cli.Commands.Restore;

public abstract class RestoreCommand
{
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

        string[] fileArgument = result.GetValue(RestoreCommandParser.SlnOrProjectOrFileArgument) ?? [];

        string[] forwardedOptions = result.OptionValuesToBeForwarded(RestoreCommandParser.GetCommand()).ToArray();

        if (fileArgument is [{ } arg] && VirtualProjectBuildingCommand.IsValidEntryPointPath(arg))
        {
            string fileBasedProgramPath = Path.GetFullPath(arg);

            VerbosityOptions? verbosity = result.GetValue(CommonOptions.VerbosityOption);

            var command = new VirtualRestoreCommand(
                entryPointFileFullPath: fileBasedProgramPath,
                binaryLoggerArgs: forwardedOptions,
                consoleLogger: RunCommand.MakeTerminalLogger(verbosity));

            CommonRunHelpers.AddUserPassedProperties(command.VirtualBuildingCommand.GlobalProperties, forwardedOptions);

            return command;
        }

        return new ForwardingRestoreCommand(
            msbuildArgs: ["-target:Restore", .. forwardedOptions, .. fileArgument],
            msbuildPath: msbuildPath);
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

    public abstract int Execute();
}

public sealed class ForwardingRestoreCommand : RestoreCommand
{
    public ForwardingRestoreCommand(IEnumerable<string> msbuildArgs, string msbuildPath = null)
    {
        ForwardingApp = new MSBuildForwardingApp(msbuildArgs, msbuildPath);
        NuGetSignatureVerificationEnabler.ConditionallyEnable(ForwardingApp);
    }

    public MSBuildForwardingApp ForwardingApp { get; }

    public override int Execute() => ForwardingApp.Execute();
}

internal sealed class VirtualRestoreCommand : RestoreCommand
{
    public VirtualRestoreCommand(string entryPointFileFullPath, string[] binaryLoggerArgs, ILogger consoleLogger)
    {
        VirtualBuildingCommand = new VirtualProjectBuildingCommand
        {
            EntryPointFileFullPath = entryPointFileFullPath,
        };
        ExecuteArgs = new VirtualProjectBuildingCommand.ExecuteArgs
        {
            BinaryLoggerArgs = binaryLoggerArgs,
            ConsoleLogger = consoleLogger,
            NoCache = true,
            NoBuild = true,
        };
    }

    public VirtualProjectBuildingCommand VirtualBuildingCommand { get; }
    public VirtualProjectBuildingCommand.ExecuteArgs ExecuteArgs { get; }

    public override int Execute() => VirtualBuildingCommand.Execute(ExecuteArgs);
}
