// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.DotNet.Cli.Commands.Run;

/// <summary>
/// A helper to perform edits of file-based app C# source files (e.g., updating the directives).
/// </summary>
/// <remarks>
/// Currently, each editor instance can be used to make at most one edit.
/// </remarks>
internal sealed class FileBasedAppSourceEditor
{
    public required SourceFile SourceFile { get; set; }
    public required ImmutableArray<CSharpDirective> Directives { get; init; }
    public required string NewLine { get; init; }

    private FileBasedAppSourceEditor() { }

    public static FileBasedAppSourceEditor Load(SourceFile sourceFile)
    {
        var directives = VirtualProjectBuildingCommand.FindDirectives(sourceFile, reportAllErrors: false, DiagnosticBag.Ignore());
        return new FileBasedAppSourceEditor
        {
            SourceFile = sourceFile,
            Directives = directives,
            NewLine = GetNewLine(sourceFile.Text),
        };

        static string GetNewLine(SourceText text)
        {
            // Try to detect existing line endings.
            string firstLine = text.Lines is [{ } line, ..]
                ? text.ToString(line.SpanIncludingLineBreak)
                : string.Empty;
            return firstLine switch
            {
                [.., '\r', '\n'] => "\r\n",
                [.., '\n'] => "\n",
                _ => Environment.NewLine,
            };
        }
    }

    public void Add(CSharpDirective directive)
    {
        string directiveText = directive.ToString() + NewLine;
        int insertPosition = DetermineWhereToAdd(directive);
        SourceFile = SourceFile.WithText(SourceFile.Text.Replace(start: insertPosition, length: 0, newText: directiveText));
    }

    private int DetermineWhereToAdd(CSharpDirective directive)
    {
        // Find the last directive of the first group of directives of the same kind.
        // If found, we will insert the new directive after it.
        CSharpDirective? addAfer = null;
        foreach (var existingDirective in Directives)
        {
            if (existingDirective.GetType() == directive.GetType())
            {
                addAfer = existingDirective;
            }
            else if (addAfer != null)
            {
                break;
            }
        }

        if (addAfer != null)
        {
            return addAfer.Span.End;
        }

        return 0;
    }
}
