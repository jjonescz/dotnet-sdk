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

    [Theory]
    [InlineData("#:package MyPackage@1.0.1")]
    [InlineData("#:package   MyPackage @ abc")]
    [InlineData("#:package MYPACKAGE")]
    public void ReplaceExisting(string inputLine)
    {
        Verify(
            $"""
            {inputLine}
            Console.WriteLine();
            """,
            static editor => editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" }),
            """
            #:package MyPackage@1.0.0
            Console.WriteLine();
            """);
    }

    [Fact]
    public void OnlyStatement()
    {
        Verify(
            """
            Console.WriteLine();
            """,
            static editor => editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" }),
            """
            #:package MyPackage@1.0.0

            Console.WriteLine();
            """);
    }

    [Fact]
    public void PreExistingWhiteSpace()
    {
        Verify(
            """


            Console.WriteLine();
            """,
            static editor => editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" }),
            """
            #:package MyPackage@1.0.0


            Console.WriteLine();
            """);
    }

    [Fact]
    public void Comments()
    {
        Verify(
            """
            // Comment1a
            // Comment1b

            // Comment2a
            // Comment2b
            Console.WriteLine();
            // Comment3
            """,
            static editor => editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" }),
            """
            // Comment1a
            // Comment1b

            // Comment2a
            // Comment2b

            #:package MyPackage@1.0.0

            Console.WriteLine();
            // Comment3
            """);
    }

    [Fact]
    public void CommentsWithWhiteSpaceAfter()
    {
        Verify(
            """
            // Comment


            Console.WriteLine();
            """,
            static editor => editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" }),
            """
            // Comment


            #:package MyPackage@1.0.0

            Console.WriteLine();
            """);
    }
    [Fact]
    public void Comment_MultiLine()
    {
        Verify(
            """
            /* test */Console.WriteLine();
            """,
            static editor => editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" }),
            """
            /* test */

            #:package MyPackage@1.0.0

            Console.WriteLine();
            """);
    }

    [Fact]
    public void Group()
    {
        Verify(
            """
            #:property A
            #:package B@C
            #:project D
            #:package E

            Console.WriteLine();
            """,
            static editor => editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" }),
            """
            #:property A
            #:package B@C
            #:package MyPackage@1.0.0
            #:project D
            #:package E

            Console.WriteLine();
            """);
    }

    [Fact]
    public void GroupWithoutSpace()
    {
        Verify(
            """
            #:package B@C
            Console.WriteLine();
            """,
            static editor => editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" }),
            """
            #:package B@C
            #:package MyPackage@1.0.0
            Console.WriteLine();
            """);
    }

    [Fact]
    public void OtherDirectives()
    {
        Verify(
            """
            #:property A
            #:project D
            Console.WriteLine();
            """,
            static editor => editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" }),
            """
            #:package MyPackage@1.0.0
            #:property A
            #:project D
            Console.WriteLine();
            """);
    }

    [Fact]
    public void AfterTokens()
    {
        Verify(
            """
            using System;

            #:package A

            Console.WriteLine();
            """,
            static editor => editor.Add(new CSharpDirective.Package { Span = default, Name = "MyPackage", Version = "1.0.0" }),
            """
            #:package MyPackage@1.0.0

            using System;

            #:package A

            Console.WriteLine();
            """);
    }

    private void Verify(
        string input,
        Action<FileBasedAppSourceEditor> action,
        string expectedOutput)
    {
        var editor = CreateEditor(input);
        action(editor);
        var actualOutput = editor.SourceFile.Text.ToString();
        if (actualOutput != expectedOutput)
        {
            Log.WriteLine("Expected output:");
            Log.WriteLine(expectedOutput);
            Log.WriteLine("\nActual output:");
            Log.WriteLine(actualOutput);
            Assert.Fail();
        }
    }
}
