// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.DotNet.Cli.Commands.Run.Api;

internal sealed class RunApiCommand(ParseResult parseResult) : CommandBase(parseResult)
{
    public override int Execute()
    {
        for (string line; (line = Console.ReadLine()) != null;)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                RunApiInput input = JsonSerializer.Deserialize(line, RunFileApiJsonSerializerContext.Default.RunApiInput);
                var sourceFile = VirtualProjectBuildingCommand.LoadSourceFile(input.EntryPointFileFullPath);
                var errors = ImmutableArray.CreateBuilder<SimpleDiagnostic>();
                var directives = VirtualProjectBuildingCommand.FindDirectives(sourceFile, reportAllErrors: true, errors);

                var csprojWriter = new StringWriter();
                VirtualProjectBuildingCommand.WriteProjectFile(csprojWriter, directives);

                Respond(new RunApiOutput.Project
                {
                    Content = csprojWriter.ToString(),
                    Diagnostics = errors.ToImmutableArray(),
                });
            }
            catch (Exception ex)
            {
                Respond(new RunApiOutput.Error { Message = ex.Message, Details = ex.ToString() });
            }
        }

        return 0;

        static void Respond(RunApiOutput message)
        {
            string json = JsonSerializer.Serialize(message, RunFileApiJsonSerializerContext.Default.RunApiOutput);
            Console.WriteLine(json);
        }
    }
}

internal sealed class RunApiInput
{
    public required string? ArtifactsPath { get; init; }
    public required string EntryPointFileFullPath { get; init; }
}

[JsonDerivedType(typeof(Error), nameof(Error))]
[JsonDerivedType(typeof(Project), nameof(Project))]
internal abstract class RunApiOutput
{
    private RunApiOutput() { }

    public sealed class Error : RunApiOutput
    {
        public required string Message { get; init; }
        public required string Details { get; init; }
    }

    public sealed class Project : RunApiOutput
    {
        public required string Content { get; init; }
        public required ImmutableArray<SimpleDiagnostic> Diagnostics { get; init; }
    }
}

[JsonSerializable(typeof(RunApiInput))]
[JsonSerializable(typeof(RunApiOutput))]
internal partial class RunFileApiJsonSerializerContext : JsonSerializerContext;
