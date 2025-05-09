// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Microsoft.DotNet.Cli.Commands.Run;

/// <summary>
/// Used to invoke C# compiler to support <c>dotnet run file.cs</c>.
/// </summary>
internal sealed class CSharpCompilerCommand
{
    private static readonly string CscPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "Roslyn", "bincore", "csc.dll");

    public required string EntryPointFileFullPath { get; init; }

    public int Execute()
    {
        return new DotNetCommandFactory()
            .Create("exec", [CscPath, EntryPointFileFullPath])
            .Execute()
            .ExitCode;
    }
}
