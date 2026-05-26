// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if !CLI_AOT
using System.Diagnostics.CodeAnalysis;
using Microsoft.Build.Execution;
#endif
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.DotNet.Cli.Commands.Run;

internal sealed record RunProperties(
    string Command,
    string? Arguments,
    string? WorkingDirectory,
    string RuntimeIdentifier,
    string DefaultAppHostRuntimeIdentifier,
    string TargetFrameworkVersion)
{
    internal RunProperties(string command, string? arguments, string? workingDirectory)
        : this(command, arguments, workingDirectory, string.Empty, string.Empty, string.Empty)
    {
    }

#if !CLI_AOT
    internal static bool TryFromProject(ProjectInstance project, [NotNullWhen(returnValue: true)] out RunProperties? result)
    {
        result = new RunProperties(
            Command: project.GetPropertyValue("RunCommand"),
            Arguments: project.GetPropertyValue("RunArguments"),
            WorkingDirectory: project.GetPropertyValue("RunWorkingDirectory"),
            RuntimeIdentifier: project.GetPropertyValue("RuntimeIdentifier"),
            DefaultAppHostRuntimeIdentifier: project.GetPropertyValue("DefaultAppHostRuntimeIdentifier"),
            TargetFrameworkVersion: project.GetPropertyValue("TargetFrameworkVersion"));

        if (string.IsNullOrEmpty(result.Command))
        {
            result = null;
            return false;
        }

        return true;
    }

    internal static RunProperties FromProject(ProjectInstance project)
    {
        if (!TryFromProject(project, out var result))
        {
            RunCommand.ThrowUnableToRunError(project);
        }

        return result;
    }
#endif

    internal RunProperties WithApplicationArguments(string[] applicationArgs)
    {
        if (applicationArgs.Length != 0)
        {
            return this with { Arguments = Arguments + " " + ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart(applicationArgs) };
        }

        return this;
    }
}
