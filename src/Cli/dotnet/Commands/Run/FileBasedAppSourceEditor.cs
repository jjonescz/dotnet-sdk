// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.DotNet.Cli.Commands.Run;

/// <summary>
/// A helper to perform edits of file-based app C# source files (e.g., updating the directives).
/// </summary>
internal sealed class FileBasedAppSourceEditor
{
    private bool _modified;

    public SourceFile SourceFile
    {
        get;
        private set
        {
            field = value;
            _modified = true;
        }
    }

    public ImmutableArray<CSharpDirective> Directives
    {
        get
        {
            ReloadIfNecessary();
            return field;
        }
        private set
        {
            field = value;
            _modified = false;
        }
    }

    public required string NewLine { get; init; }

    private FileBasedAppSourceEditor() { }

    public static FileBasedAppSourceEditor Load(SourceFile sourceFile)
    {
        return new FileBasedAppSourceEditor
        {
            SourceFile = sourceFile,
            Directives = LoadDirectives(sourceFile),
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

    private static ImmutableArray<CSharpDirective> LoadDirectives(SourceFile sourceFile)
    {
        return VirtualProjectBuildingCommand.FindDirectives(sourceFile, reportAllErrors: false, DiagnosticBag.Ignore());
    }

    private void ReloadIfNecessary()
    {
        if (_modified)
        {
            Directives = LoadDirectives(SourceFile);
        }
    }

    public void Add(CSharpDirective directive)
    {
        string directiveText = directive.ToString() + NewLine;
        TextSpan span = DetermineWhereToAdd(directive);
        SourceFile = SourceFile.WithText(SourceFile.Text.Replace(span, newText: directiveText));
    }

    private TextSpan DetermineWhereToAdd(CSharpDirective directive)
    {
        // Find one that has the same kind and name.
        // If found, we will replace it with the new directive.
        if (directive is CSharpDirective.Named named &&
            Directives.OfType<CSharpDirective.Named>().FirstOrDefault(d => NamedDirectiveComparer.Instance.Equals(d, named)) is { } toReplace)
        {
            return toReplace.Span;
        }

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
            return new TextSpan(start: addAfer.Span.End, length: 0);
        }

        return new TextSpan(start: 0, length: 0);
    }
}
