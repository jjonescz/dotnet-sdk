// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis.Text;
using Microsoft.DotNet.Cli.Commands.Run;

namespace Microsoft.DotNet.Cli.Run.Tests;

public sealed class FileBasedAppSourceEditorTests(ITestOutputHelper log) : SdkTest(log)
{
    private static FileBasedAppSourceEditor CreateEditor(string source)
    {
        return FileBasedAppSourceEditor.Load(new SourceFile("/app/Program.cs", SourceText.From(source, Encoding.UTF8)));
    }

    [Fact]
    public void OnlyStatement()
    {
        var editor = CreateEditor("""
            Console.WriteLine();
            """);
        editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" });
        editor.SourceFile.Text.ToString().Should().Be("""
            #:package MyPackage@1.0.0

            Console.WriteLine();
            """);
    }

    [Fact]
    public void PreExistingWhiteSpace()
    {
        var editor = CreateEditor("""


            Console.WriteLine();
            """);
        editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" });
        editor.SourceFile.Text.ToString().Should().Be("""
            #:package MyPackage@1.0.0


            Console.WriteLine();
            """);
    }

    [Fact]
    public void Comments()
    {
        var editor = CreateEditor("""
            // Comment1a
            // Comment1b

            // Comment2a
            // Comment2b
            Console.WriteLine();
            """);
        editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" });
        editor.SourceFile.Text.ToString().Should().Be("""
            // Comment1a
            // Comment1b

            // Comment2a
            // Comment2b

            #:package MyPackage@1.0.0

            Console.WriteLine();
            """);
    }

    [Fact]
    public void CommentsWithWhiteSpaceAfter()
    {
        var editor = CreateEditor("""
            // Comment


            Console.WriteLine();
            """);
        editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" });
        editor.SourceFile.Text.ToString().Should().Be("""
            // Comment


            #:package MyPackage@1.0.0

            Console.WriteLine();
            """);
    }

    [Fact]
    public void Group()
    {
        var editor = CreateEditor("""
            #:property A
            #:package B@C
            #:project D
            #:package E

            Console.WriteLine();
            """);
        editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" });
        editor.SourceFile.Text.ToString().Should().Be("""
            #:property A
            #:package B@C
            #:package MyPackage@1.0.0
            #:project D
            #:package E

            Console.WriteLine();
            """);
    }

    [Fact]
    public void AfterTokens()
    {
        var editor = CreateEditor("""
            using System;

            #:package A

            Console.WriteLine();
            """);
        editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" });
        editor.SourceFile.Text.ToString().Should().Be("""
            #:package MyPackage@1.0.0

            using System;

            #:package A

            Console.WriteLine();
            """);
    }
}
