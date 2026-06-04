// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Microsoft.Build.CommandLine.Experimental;
using Microsoft.DotNet.Cli.CommandLine;
using Microsoft.DotNet.Cli.Commands.Run;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Cli.Utils.Extensions;
using Microsoft.DotNet.ProjectTools;

namespace Microsoft.DotNet.Cli.Commands;

public static class CommandFactory
{
    internal static CommandBase CreateVirtualOrPhysicalCommand(
        System.CommandLine.Command commandDefinition,
        Argument<string[]> catchAllUserInputArgument,
        Func<MSBuildArgs, string, VirtualProjectBuildingCommand> createVirtualCommand,
        Func<MSBuildArgs, string?, CommandBase> createPhysicalCommand,
        IEnumerable<Option> optionsToUseWhenParsingMSBuildFlags,
        ParseResult parseResult,
        string? msbuildPath = null,
        Func<MSBuildArgs, MSBuildArgs>? transformer = null)
    {
        var args = parseResult.GetValue(catchAllUserInputArgument) ?? [];
        LoggerUtility.SeparateBinLogArguments(args, out _, out var nonBinLogArgs);
        var forwardedArgs = parseResult.OptionValuesToBeForwarded(commandDefinition);
        if (TryGetSingleProjectArg(forwardedArgs, args, out var arg, out var userSpecifiedMSBuildArgs)
            && VirtualProjectBuilder.IsValidEntryPointPath(arg))
        {
            var msbuildArgs = MSBuildArgs.AnalyzeMSBuildArguments([.. forwardedArgs, .. userSpecifiedMSBuildArgs],
            [
                .. optionsToUseWhenParsingMSBuildFlags,
                CommonOptions.CreateGetPropertyOption(),
                CommonOptions.CreateGetItemOption(),
                CommonOptions.CreateGetTargetResultOption(),
                CommonOptions.CreateGetResultOutputFileOption(),
            ]);
            msbuildArgs = transformer?.Invoke(msbuildArgs) ?? msbuildArgs;
            return createVirtualCommand(msbuildArgs, Path.GetFullPath(arg));
        }
        else
        {
            // Warn if any argument looks like a file-based program entry point but we're falling back to MSBuild.
            // This can happen when extra positional arguments prevent the single-arg file-based path from being taken,
            // or when a .cs file doesn't exist (so IsValidEntryPointPath returns false).
            foreach (var candidate in nonBinLogArgs)
            {
                if (VirtualProjectBuilder.IsValidEntryPointPath(candidate))
                {
                    Reporter.Error.WriteLine(
                        string.Format(CliCommandStrings.WarningFileArgumentPassedToMSBuild, candidate, commandDefinition.Name).Yellow());
                    break;
                }

                if (candidate.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    Reporter.Error.WriteLine(
                        string.Format(CliCommandStrings.WarningCsFileArgumentPassedToMSBuild, candidate).Yellow());
                    break;
                }
            }

            var msbuildArgs = MSBuildArgs.AnalyzeMSBuildArguments([.. forwardedArgs, .. args], [.. optionsToUseWhenParsingMSBuildFlags]);
            msbuildArgs = transformer?.Invoke(msbuildArgs) ?? msbuildArgs;
            return createPhysicalCommand(msbuildArgs, msbuildPath);
        }
    }

    private static bool TryGetSingleProjectArg(
        IEnumerable<string> forwardedArgs,
        string[] args,
        out string projectArg,
        out string[] argsWithoutProject)
    {
        projectArg = string.Empty;
        argsWithoutProject = args;

        try
        {
            var parsedMSBuildArgs = new CommandLineParser().Parse([.. forwardedArgs, .. args]);
            if (parsedMSBuildArgs.Project is not [{ } parsedProjectArg])
            {
                return false;
            }

            if (!TryRemoveFirstArg(args, parsedProjectArg, out argsWithoutProject))
            {
                return false;
            }

            projectArg = parsedProjectArg;
            return true;
        }
        catch (CommandLineSwitchException)
        {
            return false;
        }
    }

    private static bool TryRemoveFirstArg(string[] args, string argToRemove, out string[] remainingArgs)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], argToRemove, StringComparison.Ordinal))
            {
                remainingArgs = [.. args[..i], .. args[(i + 1)..]];
                return true;
            }
        }

        remainingArgs = args;
        return false;
    }
}
