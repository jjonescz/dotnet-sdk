// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.FileBasedPrograms;

/// <summary>
/// Represents a C# directive starting with <c>#:</c>.
/// Those are ignored by the language but recognized by file-based programs.
/// </summary>
internal abstract class FileBasedProgramDirective
{
    private FileBasedProgramDirective() { }

    /// <summary>
    /// Span of the full line including the trailing line break.
    /// </summary>
    public required TextSpan Span { get; init; }

    /// <param name="span">
    /// See <see cref="Span"/>. This is the full span that will be removed during file-based to project-based conversion
    /// unlike in <paramref name="locationInfo"/> which is only used for diagnostics and can contain any user-friendly span.
    /// </param>
    public static FileBasedProgramDirective? TryParse(in LocationInfo locationInfo, TextSpan span, string directiveKind, string directiveText, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch (directiveKind)
        {
            case "sdk": return Sdk.TryParseOne(locationInfo, span, directiveKind, directiveText, diagnostics);
            case "property": return Property.TryParseOne(locationInfo, span, directiveKind, directiveText, diagnostics);
            case "package": return Package.TryParseOne(locationInfo, span, directiveKind, directiveText, diagnostics);
            default:
                diagnostics.Add(Diagnostic.Create(FileBasedProgramDiagnostics.UnrecognizedDirective, locationInfo.ToLocation(), directiveKind));
                return null;
        }
    }

    private static (string, string?)? TryParseOptionalTwoParts(in LocationInfo locationInfo, string directiveKind, string directiveText, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var i = directiveText.IndexOf(' ', StringComparison.Ordinal);
        var firstPart = i < 0 ? directiveText : directiveText[..i];

        if (string.IsNullOrWhiteSpace(firstPart))
        {
            diagnostics.Add(Diagnostic.Create(FileBasedProgramDiagnostics.MissingDirectiveName, locationInfo.ToLocation(), directiveKind));
            return null;
        }

        var secondPart = i < 0 ? [] : directiveText.AsSpan(i + 1).TrimStart();
        if (i < 0 || secondPart.IsWhiteSpace())
        {
            return (firstPart, null);
        }

        return (firstPart, secondPart.ToString());
    }

    /// <summary>
    /// <c>#!</c> directive.
    /// </summary>
    public sealed class Shebang : FileBasedProgramDirective;

    /// <summary>
    /// <c>#:sdk</c> directive.
    /// </summary>
    public sealed class Sdk : FileBasedProgramDirective
    {
        private Sdk() { }

        public required string Name { get; init; }
        public string? Version { get; init; }

        public static Sdk? TryParseOne(in LocationInfo locationInfo, TextSpan span, string directiveKind, string directiveText, ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            var parts = TryParseOptionalTwoParts(locationInfo, directiveKind, directiveText, diagnostics);

            if (parts is not var (sdkName, sdkVersion))
            {
                return null;
            }

            return new Sdk
            {
                Span = span,
                Name = sdkName,
                Version = sdkVersion,
            };
        }

        public string ToSlashDelimitedString()
        {
            return Version is null ? Name : $"{Name}/{Version}";
        }
    }

    /// <summary>
    /// <c>#:property</c> directive.
    /// </summary>
    public sealed class Property : FileBasedProgramDirective
    {
        private Property() { }

        public required string Name { get; init; }
        public required string Value { get; init; }

        public static Property? TryParseOne(in LocationInfo locationInfo, TextSpan span, string directiveKind, string directiveText, ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            var parts = TryParseOptionalTwoParts(locationInfo, directiveKind, directiveText, diagnostics);

            if (parts is not var (propertyName, propertyValue))
            {
                return null;
            }

            if (propertyValue is null)
            {
                diagnostics.Add(Diagnostic.Create(FileBasedProgramDiagnostics.PropertyDirectiveMissingParts, locationInfo.ToLocation()));
                return null;
            }

            try
            {
                propertyName = XmlConvert.VerifyName(propertyName);
            }
            catch (XmlException ex)
            {
                diagnostics.Add(Diagnostic.Create(FileBasedProgramDiagnostics.PropertyDirectiveInvalidName, locationInfo.ToLocation(), ex.Message));
                return null;
            }

            return new Property
            {
                Span = span,
                Name = propertyName,
                Value = propertyValue,
            };
        }
    }

    /// <summary>
    /// <c>#:package</c> directive.
    /// </summary>
    public sealed class Package : FileBasedProgramDirective
    {
        private Package() { }

        public required string Name { get; init; }
        public string? Version { get; init; }

        public static Package? TryParseOne(in LocationInfo locationInfo, TextSpan span, string directiveKind, string directiveText, ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            var parts = TryParseOptionalTwoParts(locationInfo, directiveKind, directiveText, diagnostics);

            if (parts is not var (packageName, packageVersion))
            {
                return null;
            }

            return new Package
            {
                Span = span,
                Name = packageName,
                Version = packageVersion,
            };
        }
    }
}

#pragma warning disable RS2008 // Enable analyzer release tracking
#pragma warning disable RS1029 // Do not use reserved diagnostic IDs
internal static class FileBasedProgramDiagnostics
{
    public static readonly DiagnosticDescriptor UnrecognizedDirective = new(
        id: "CS9308",
        title: null,
        messageFormat: "Unrecognized directive '{0}'",
        category: "Compiler",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingDirectiveName = new(
        id: "CS9309",
        title: null,
        messageFormat: "Missing name of '{0}'",
        category: "Compiler",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PropertyDirectiveMissingParts = new(
        id: "CS9310",
        title: null,
        messageFormat: "The property directive needs to have two parts separated by a space like 'PropertyName PropertyValue'",
        category: "Compiler",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PropertyDirectiveInvalidName = new(
        id: "CS9311",
        title: null,
        messageFormat: "Invalid property name: {0}",
        category: "Compiler",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CannotConvertDirective = new(
        id: "CS9312",
        title: null,
        messageFormat: "This directive cannot be converted. Run the file to see more details.",
        category: "Compiler",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
