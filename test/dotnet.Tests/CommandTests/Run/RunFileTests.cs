// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using System.Text.Json;
using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.DotNet.Cli.Commands;
using Microsoft.DotNet.Cli.Commands.Run;
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.DotNet.Cli.Run.Tests;

public sealed class RunFileTests(ITestOutputHelper log) : SdkTest(log)
{
    private static readonly string s_program = """
        if (args.Length > 0)
        {
            Console.WriteLine("echo args:" + string.Join(";", args));
        }
        Console.WriteLine("Hello from " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Name);
        #if !DEBUG
        Console.WriteLine("Release config");
        #endif
        #if CUSTOM_DEFINE
        Console.WriteLine("Custom define");
        #endif
        """;

    private static readonly string s_programDependingOnUtil = """
        if (args.Length > 0)
        {
            Console.WriteLine("echo args:" + string.Join(";", args));
        }
        Console.WriteLine("Hello, " + Util.GetMessage());
        """;

    private static readonly string s_util = """
        static class Util
        {
            public static string GetMessage()
            {
                return "String from Util";
            }
        }
        """;

    private static readonly string s_consoleProject = $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>{ToolsetInfo.CurrentTargetFramework}</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private static readonly string s_launchSettings = """
        {
            "profiles": {
                "TestProfile1": {
                    "commandName": "Project",
                    "environmentVariables": {
                        "Message": "TestProfileMessage1"
                    }
                },
                "TestProfile2": {
                    "commandName": "Project",
                    "environmentVariables": {
                        "Message": "TestProfileMessage2"
                    }
                }
            }
        }
        """;

    /// <summary>
    /// Used when we need an out-of-tree base test directory to avoid having implicit build files
    /// like Directory.Build.props in scope and negating the optimizations we want to test.
    /// </summary>
    private static readonly string s_outOfTreeBaseDirectory = Path.Join(Path.GetTempPath(), "dotnetSdkTests");

    private static bool HasCaseInsensitiveFileSystem
    {
        get
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        }
    }

    /// <summary>
    /// <c>dotnet run file.cs</c> succeeds without a project file.
    /// </summary>
    [Theory]
    [InlineData(null, false)] // will be replaced with an absolute path
    [InlineData("Program.cs", false)]
    [InlineData("./Program.cs", false)]
    [InlineData("Program.CS", true)]
    public void FilePath(string? path, bool differentCasing)
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();

        var programPath = Path.Join(testInstance.Path, "Program.cs");

        File.WriteAllText(programPath, s_program);

        path ??= programPath;

        var result = new DotnetCommand(Log, "run", path)
            .WithWorkingDirectory(testInstance.Path)
            .Execute();

        if (!differentCasing || HasCaseInsensitiveFileSystem)
        {
            result.Should().Pass()
                .And.HaveStdOut("Hello from Program");
        }
        else
        {
            result.Should().Fail()
                .And.HaveStdErrContaining(string.Format(
                    CliCommandStrings.RunCommandExceptionNoProjects,
                    testInstance.Path,
                    RunCommandParser.ProjectOption.Name));
        }
    }

    /// <summary>
    /// <c>dotnet file.cs</c> is equivalent to <c>dotnet run file.cs</c>.
    /// </summary>
    [Fact]
    public void FilePath_WithoutRun()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        new DotnetCommand(Log, "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("""
                Hello from Program
                """);

        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), $"""
            #:property Configuration=Release
            {s_program}
            """);

        new DotnetCommand(Log, "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("""
                Hello from Program
                Release config
                """);

        new DotnetCommand(Log, "Program.cs", "-c", "Debug")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("""
                Hello from Program
                """);
    }

    /// <summary>
    /// Casing of the argument is used for the output binary name.
    /// </summary>
    [Fact]
    public void FilePath_DifferentCasing()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        var result = new DotnetCommand(Log, "run", "program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute();

        if (HasCaseInsensitiveFileSystem)
        {
            result.Should().Pass()
                .And.HaveStdOut("Hello from program");
        }
        else
        {
            result.Should().Fail()
                .And.HaveStdErrContaining(string.Format(
                    CliCommandStrings.RunCommandExceptionNoProjects,
                    testInstance.Path,
                    RunCommandParser.ProjectOption.Name));
        }
    }

    /// <summary>
    /// <c>dotnet run folder/file.cs</c> succeeds without a project file.
    /// </summary>
    [Fact]
    public void FilePath_OutsideWorkDir()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        var dirName = Path.GetFileName(testInstance.Path);

        new DotnetCommand(Log, "run", $"{dirName}/Program.cs")
            .WithWorkingDirectory(Path.GetDirectoryName(testInstance.Path)!)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program");
    }

    /// <summary>
    /// <c>dotnet run --project file.cs</c> fails.
    /// </summary>
    [Fact]
    public void FilePath_AsProjectArgument()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        new DotnetCommand(Log, "run", "--project", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(CliCommandStrings.RunCommandException);
    }

    /// <summary>
    /// <c>dotnet run folder</c> without a project file is not supported.
    /// </summary>
    [Theory]
    [InlineData(null)] // will be replaced with an absolute path
    [InlineData(".")]
    [InlineData("../MSBuildTestApp")]
    [InlineData("../MSBuildTestApp/")]
    public void FolderPath(string? path)
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        path ??= testInstance.Path;

        new DotnetCommand(Log, "run", path)
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(
                CliCommandStrings.RunCommandExceptionNoProjects,
                testInstance.Path,
                RunCommandParser.ProjectOption.Name));
    }

    /// <summary>
    /// <c>dotnet run app.csproj</c> fails if app.csproj does not exist.
    /// </summary>
    [Fact]
    public void ProjectPath_DoesNotExist()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        new DotnetCommand(Log, "run", "./App.csproj")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(
                CliCommandStrings.RunCommandExceptionNoProjects,
                testInstance.Path,
                RunCommandParser.ProjectOption.Name));
    }

    /// <summary>
    /// <c>dotnet run app.csproj</c> where app.csproj exists
    /// runs the project and passes 'app.csproj' as an argument.
    /// </summary>
    [Fact]
    public void ProjectPath_Exists()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);
        File.WriteAllText(Path.Join(testInstance.Path, "App.csproj"), s_consoleProject);

        new DotnetCommand(Log, "run", "./App.csproj")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOutContaining("""
                echo args:./App.csproj
                Hello from App
                """);
    }

    /// <summary>
    /// When a file is not a .cs file, we probe the first characters of the file for <c>#!</c>, and
    /// execute as a single file program if we find them.
    /// </summary>
    [Theory]
    [InlineData("Program")]
    [InlineData("Program.csx")]
    [InlineData("Program.vb")]
    public void NonCsFileExtensionWithShebang(string fileName)
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, fileName), """
            #!/usr/bin/env dotnet
            Console.WriteLine("hello world");
            """);

        new DotnetCommand(Log, "run", fileName)
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOutContaining("hello world");
    }

    /// <summary>
    /// When a file is not a .cs file, we probe the first characters of the file for <c>#!</c>, and
    /// fall back to normal <c>dotnet run</c> behavior if we don't find them.
    /// </summary>
    [Theory]
    [InlineData("Program")]
    [InlineData("Program.csx")]
    [InlineData("Program.vb")]
    public void NonCsFileExtensionWithNoShebang(string fileName)
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, fileName), s_program);

        new DotnetCommand(Log, "run", fileName)
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(
                CliCommandStrings.RunCommandExceptionNoProjects,
                testInstance.Path,
                RunCommandParser.ProjectOption.Name));
    }

    [Fact]
    public void MultipleEntryPoints()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);
        File.WriteAllText(Path.Join(testInstance.Path, "Program2.cs"), s_program);

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program");

        new DotnetCommand(Log, "run", "Program2.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program2");
    }

    /// <summary>
    /// When the entry-point file does not exist, fallback to normal <c>dotnet run</c> behavior.
    /// </summary>
    [Fact]
    public void NoCode()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(
                CliCommandStrings.RunCommandExceptionNoProjects,
                testInstance.Path,
                RunCommandParser.ProjectOption.Name));
    }

    /// <summary>
    /// Cannot run a non-entry-point file.
    /// </summary>
    [Fact]
    public void ClassLibrary_EntryPointFileExists()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Util.cs"), s_util);

        new DotnetCommand(Log, "run", "Util.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdOutContaining("error CS5001:"); // Program does not contain a static 'Main' method suitable for an entry point
    }

    /// <summary>
    /// When the entry-point file does not exist, fallback to normal <c>dotnet run</c> behavior.
    /// </summary>
    [Fact]
    public void ClassLibrary_EntryPointFileDoesNotExist()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Util.cs"), s_util);

        new DotnetCommand(Log, "run", "NonExistentFile.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(
                CliCommandStrings.RunCommandExceptionNoProjects,
                testInstance.Path,
                RunCommandParser.ProjectOption.Name));
    }

    /// <summary>
    /// Other files in the folder are not part of the compilation.
    /// </summary>
    [Fact]
    public void MultipleFiles_RunEntryPoint()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_programDependingOnUtil);
        File.WriteAllText(Path.Join(testInstance.Path, "Util.cs"), s_util);

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdOutContaining("error CS0103"); // The name 'Util' does not exist in the current context
    }

    /// <summary>
    /// <c>dotnet run util.cs</c> fails if <c>util.cs</c> is not the entry-point.
    /// </summary>
    [Fact]
    public void MultipleFiles_RunLibraryFile()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_programDependingOnUtil);
        File.WriteAllText(Path.Join(testInstance.Path, "Util.cs"), s_util);

        new DotnetCommand(Log, "run", "Util.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdOutContaining("error CS5001:"); // Program does not contain a static 'Main' method suitable for an entry point
    }

    /// <summary>
    /// If there are nested project files like
    /// <code>
    ///  app/file.cs
    ///  app/nested/x.csproj
    ///  app/nested/another.cs
    /// </code>
    /// executing <c>dotnet run app/file.cs</c> will include the nested <c>.cs</c> file in the compilation.
    /// Hence we could consider reporting an error in this situation.
    /// However, the same problem exists for normal builds with explicit project files
    /// and usually the build fails because there are multiple entry points or other clashes.
    /// </summary>
    [Fact]
    public void NestedProjectFiles()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);
        Directory.CreateDirectory(Path.Join(testInstance.Path, "nested"));
        File.WriteAllText(Path.Join(testInstance.Path, "nested", "App.csproj"), s_consoleProject);

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program");
    }

    /// <summary>
    /// <c>dotnet run folder/app.csproj</c> -> the argument is not recognized as an entry-point file
    /// (it does not have <c>.cs</c> file extension), so this fallbacks to normal <c>dotnet run</c> behavior.
    /// </summary>
    [Fact]
    public void RunNestedProjectFile()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);
        File.WriteAllText(Path.Join(testInstance.Path, "App.csproj"), s_consoleProject);

        var dirName = Path.GetFileName(testInstance.Path);

        var workDir = Path.GetDirectoryName(testInstance.Path)!;

        new DotnetCommand(Log, "run", $"{dirName}/App.csproj")
            .WithWorkingDirectory(workDir)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(
                CliCommandStrings.RunCommandExceptionNoProjects,
                workDir,
                RunCommandParser.ProjectOption.Name));
    }

    /// <summary>
    /// Main method is supported just like top-level statements.
    /// </summary>
    [Fact]
    public void MainMethod()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp").WithSource();
        File.Delete(Path.Join(testInstance.Path, "MSBuildTestApp.csproj"));

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello World!");
    }

    /// <summary>
    /// Empty file does not contain entry point, so that's an error.
    /// </summary>
    [Fact]
    public void EmptyFile()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), string.Empty);

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdOutContaining("error CS5001:"); // Program does not contain a static 'Main' method suitable for an entry point
    }

    /// <summary>
    /// Implicit build files have an effect.
    /// </summary>
    [Fact]
    public void DirectoryBuildProps()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);
        File.WriteAllText(Path.Join(testInstance.Path, "Directory.Build.props"), """
            <Project>
                <PropertyGroup>
                    <AssemblyName>TestName</AssemblyName>
                </PropertyGroup>
            </Project>
            """);

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from TestName");
    }

    /// <summary>
    /// Command-line arguments should be passed through.
    /// </summary>
    [Theory]
    [InlineData("other;args", "other;args")]
    [InlineData("--;other;args", "other;args")]
    [InlineData("--appArg", "--appArg")]
    [InlineData("-c;Debug;--xyz", "--xyz")]
    public void Arguments_PassThrough(string input, string output)
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        new DotnetCommand(Log, ["run", "Program.cs", .. input.Split(';')])
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut($"""
                echo args:{output}
                Hello from Program
                """);
    }

    /// <summary>
    /// <c>dotnet run --unknown-arg file.cs</c> fallbacks to normal <c>dotnet run</c> behavior.
    /// </summary>
    [Fact]
    public void Arguments_Unrecognized()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        new DotnetCommand(Log, ["run", "--arg", "Program.cs"])
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(
                CliCommandStrings.RunCommandExceptionNoProjects,
                testInstance.Path,
                RunCommandParser.ProjectOption.Name));
    }

    /// <summary>
    /// <c>dotnet run --some-known-arg file.cs</c> is supported.
    /// </summary>
    [Theory, CombinatorialData]
    public void Arguments_Recognized(bool beforeFile)
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        string[] args = beforeFile
            ? ["run", "-c", "Release", "Program.cs", "more", "args"]
            : ["run", "Program.cs", "-c", "Release", "more", "args"];

        new DotnetCommand(Log, args)
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("""
                echo args:more;args
                Hello from Program
                Release config
                """);
    }

    /// <summary>
    /// <c>dotnet run --bl file.cs</c> produces a binary log.
    /// </summary>
    [Theory, CombinatorialData]
    public void BinaryLog(bool beforeFile)
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        string[] args = beforeFile
            ? ["run", "-bl", "Program.cs"]
            : ["run", "Program.cs", "-bl"];

        new DotnetCommand(Log, args)
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program");

        new DirectoryInfo(testInstance.Path)
            .EnumerateFiles("*.binlog", SearchOption.TopDirectoryOnly)
            .Select(f => f.Name)
            .Should().BeEquivalentTo(["msbuild.binlog", "msbuild-dotnet-run.binlog"]);
    }

    [Theory, CombinatorialData]
    public void BinaryLog_Build([CombinatorialValues("restore", "build")] string command, bool beforeFile)
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        string[] args = beforeFile
            ? [command, "-bl", "Program.cs"]
            : [command, "Program.cs", "-bl"];

        new DotnetCommand(Log, args)
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass();

        new DirectoryInfo(testInstance.Path)
            .EnumerateFiles("*.binlog", SearchOption.TopDirectoryOnly)
            .Select(f => f.Name)
            .Should().BeEquivalentTo(["msbuild.binlog"]);
    }

    [Theory]
    [InlineData("-bl")]
    [InlineData("-BL")]
    [InlineData("-bl:msbuild.binlog")]
    [InlineData("/bl")]
    [InlineData("/bl:msbuild.binlog")]
    [InlineData("--binaryLogger")]
    [InlineData("--binaryLogger:msbuild.binlog")]
    [InlineData("-bl:another.binlog")]
    public void BinaryLog_ArgumentForms(string arg)
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        new DotnetCommand(Log, "run", "Program.cs", arg)
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program");

        var fileName = arg.Split(':', 2) is [_, { Length: > 0 } value] ? Path.GetFileNameWithoutExtension(value) : "msbuild";

        new DirectoryInfo(testInstance.Path)
            .EnumerateFiles("*.binlog", SearchOption.TopDirectoryOnly)
            .Select(f => f.Name)
            .Should().BeEquivalentTo([$"{fileName}.binlog", $"{fileName}-dotnet-run.binlog"]);
    }

    [Fact]
    public void BinaryLog_Multiple()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        new DotnetCommand(Log, "run", "Program.cs", "-bl:one.binlog", "two.binlog", "/bl:three.binlog")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("""
                echo args:two.binlog
                Hello from Program
                """);

        new DirectoryInfo(testInstance.Path)
            .EnumerateFiles("*.binlog", SearchOption.TopDirectoryOnly)
            .Select(f => f.Name)
            .Should().BeEquivalentTo(["three.binlog", "three-dotnet-run.binlog"]);
    }

    [Fact]
    public void BinaryLog_WrongExtension()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        new DotnetCommand(Log, "run", "Program.cs", "-bl:test.test")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("test.test"); // Invalid binary logger parameter(s): "test.test"

        new DirectoryInfo(testInstance.Path)
            .EnumerateFiles("*.binlog", SearchOption.TopDirectoryOnly)
            .Select(f => f.Name)
            .Should().BeEmpty();
    }

    /// <summary>
    /// <c>dotnet run file.cs</c> should not produce a binary log.
    /// </summary>
    [Fact]
    public void BinaryLog_NotSpecified()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program");

        new DirectoryInfo(testInstance.Path)
            .EnumerateFiles("*.binlog", SearchOption.TopDirectoryOnly)
            .Select(f => f.Name)
            .Should().BeEmpty();
    }

    /// <summary>
    /// Default projects do not include anything apart from the entry-point file.
    /// </summary>
    [Fact]
    public void EmbeddedResource()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Program.Resources.resources");

            if (stream is null)
            {
                Console.WriteLine("Resource not found");
                return;
            }

            using var reader = new System.Resources.ResourceReader(stream);
            Console.WriteLine(reader.Cast<System.Collections.DictionaryEntry>().Single());
            """);
        File.WriteAllText(Path.Join(testInstance.Path, "Resources.resx"), """
            <root>
              <data name="MyString">
                <value>TestValue</value>
              </data>
            </root>
            """);

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("""
                Resource not found
                """);
    }

    [Fact]
    public void NoRestore_01()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var programFile = Path.Join(testInstance.Path, "Program.cs");
        File.WriteAllText(programFile, s_program);

        // Remove artifacts from possible previous runs of this test.
        var artifactsDir = VirtualProjectBuildingCommand.GetArtifactsPath(programFile);
        if (Directory.Exists(artifactsDir)) Directory.Delete(artifactsDir, recursive: true);

        // It is an error when never restored before.
        new DotnetCommand(Log, "run", "--no-restore", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdOutContaining("NETSDK1004"); // error NETSDK1004: Assets file '...\obj\project.assets.json' not found. Run a NuGet package restore to generate this file.

        // Run restore.
        new DotnetCommand(Log, "restore", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass();

        // --no-restore works.
        new DotnetCommand(Log, "run", "--no-restore", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program");
    }

    [Fact]
    public void NoRestore_02()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var programFile = Path.Join(testInstance.Path, "Program.cs");
        File.WriteAllText(programFile, s_program);

        // Remove artifacts from possible previous runs of this test.
        var artifactsDir = VirtualProjectBuildingCommand.GetArtifactsPath(programFile);
        if (Directory.Exists(artifactsDir)) Directory.Delete(artifactsDir, recursive: true);

        // It is an error when never restored before.
        new DotnetCommand(Log, "build", "--no-restore", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdOutContaining("NETSDK1004"); // error NETSDK1004: Assets file '...\obj\project.assets.json' not found. Run a NuGet package restore to generate this file.

        // Run restore.
        new DotnetCommand(Log, "restore", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass();

        // --no-restore works.
        new DotnetCommand(Log, "build", "--no-restore", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass();

        new DotnetCommand(Log, "run", "--no-build", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program");
    }

    [Fact]
    public void NoBuild_01()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var programFile = Path.Join(testInstance.Path, "Program.cs");
        File.WriteAllText(programFile, s_program);

        // Remove artifacts from possible previous runs of this test.
        var artifactsDir = VirtualProjectBuildingCommand.GetArtifactsPath(programFile);
        if (Directory.Exists(artifactsDir)) Directory.Delete(artifactsDir, recursive: true);

        // It is an error when never built before.
        new DotnetCommand(Log, "run", "--no-build", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("An error occurred trying to start process");

        // Now build it.
        new DotnetCommand(Log, "build", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass();

        // Changing the program has no effect when it is not built.
        File.WriteAllText(programFile, """Console.WriteLine("Changed");""");
        new DotnetCommand(Log, "run", "--no-build", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program");

        // The change has an effect when built again.
        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Changed");
    }

    [Fact]
    public void NoBuild_02()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var programFile = Path.Join(testInstance.Path, "Program.cs");
        File.WriteAllText(programFile, s_program);

        // Remove artifacts from possible previous runs of this test.
        var artifactsDir = VirtualProjectBuildingCommand.GetArtifactsPath(programFile);
        if (Directory.Exists(artifactsDir)) Directory.Delete(artifactsDir, recursive: true);

        // It is an error when never built before.
        new DotnetCommand(Log, "run", "--no-build", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("An error occurred trying to start process");

        // Now build it.
        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program");

        // Changing the program has no effect when it is not built.
        File.WriteAllText(programFile, """Console.WriteLine("Changed");""");
        new DotnetCommand(Log, "run", "--no-build", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program");

        // The change has an effect when built again.
        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Changed");
    }

    [Fact]
    public void Publish()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var programFile = Path.Join(testInstance.Path, "Program.cs");
        File.WriteAllText(programFile, s_program);

        var artifactsDir = VirtualProjectBuildingCommand.GetArtifactsPath(programFile);
        if (Directory.Exists(artifactsDir)) Directory.Delete(artifactsDir, recursive: true);

        new DotnetCommand(Log, "publish", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass();

        new DirectoryInfo(artifactsDir).Sub("publish/release")
            .Should().Exist()
            .And.NotHaveFile("Program.deps.json"); // no deps.json file for AOT-published app
    }

    [Fact]
    public void Publish_Options()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var programFile = Path.Join(testInstance.Path, "Program.cs");
        File.WriteAllText(programFile, s_program);

        var artifactsDir = VirtualProjectBuildingCommand.GetArtifactsPath(programFile);
        if (Directory.Exists(artifactsDir)) Directory.Delete(artifactsDir, recursive: true);

        new DotnetCommand(Log, "publish", "Program.cs", "-c", "Debug", "-p:PublishAot=false", "-bl")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass();

        new DirectoryInfo(artifactsDir).Sub("publish/debug")
            .Should().Exist()
            .And.HaveFile("Program.deps.json");

        new DirectoryInfo(testInstance.Path).File("msbuild.binlog").Should().Exist();
    }

    [PlatformSpecificFact(TestPlatforms.AnyUnix), UnsupportedOSPlatform("windows")]
    public void ArtifactsDirectory_Permissions()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var programFile = Path.Join(testInstance.Path, "Program.cs");
        File.WriteAllText(programFile, s_program);

        // Remove artifacts from possible previous runs of this test.
        var artifactsDir = VirtualProjectBuildingCommand.GetArtifactsPath(programFile);
        if (Directory.Exists(artifactsDir)) Directory.Delete(artifactsDir, recursive: true);

        new DotnetCommand(Log, "build", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass();

        new DirectoryInfo(artifactsDir).UnixFileMode
            .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, artifactsDir);

        // Re-create directory with incorrect permissions.
        Directory.Delete(artifactsDir, recursive: true);
        Directory.CreateDirectory(artifactsDir, UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
        var actualMode = new DirectoryInfo(artifactsDir).UnixFileMode
            .Should().NotBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, artifactsDir).And.Subject;

        new DotnetCommand(Log, "build", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("build-start.cache"); // Unhandled exception: Access to the path '.../build-start.cache' is denied.

        // Build shouldn't have changed the permissions.
        new DirectoryInfo(artifactsDir).UnixFileMode
            .Should().Be(actualMode, artifactsDir);
    }

    [Theory, CombinatorialData]
    public void LaunchProfile(bool cscOnly)
    {
        var testInstance = _testAssetsManager.CreateTestDirectory(baseDirectory: cscOnly ? s_outOfTreeBaseDirectory : null);
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program + """

            Console.WriteLine($"Message: '{Environment.GetEnvironmentVariable("Message")}'");
            """);
        Directory.CreateDirectory(Path.Join(testInstance.Path, "Properties"));
        File.WriteAllText(Path.Join(testInstance.Path, "Properties", "launchSettings.json"), s_launchSettings);

        var prefix = cscOnly
            ? CliCommandStrings.NoBinaryLogBecauseRunningJustCsc + Environment.NewLine
            : string.Empty;

        new DotnetCommand(Log, "run", "-bl", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOutContaining(prefix + """
                Hello from Program
                Message: 'TestProfileMessage1'
                """);

        prefix = CliCommandStrings.NoBinaryLogBecauseUpToDate + Environment.NewLine;

        new DotnetCommand(Log, "run", "-bl", "--no-launch-profile", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut(prefix + """
                Hello from Program
                Message: ''
                """);

        new DotnetCommand(Log, "run", "-bl", "-lp", "TestProfile2", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOutContaining(prefix + """
                Hello from Program
                Message: 'TestProfileMessage2'
                """);
    }

    [Fact]
    public void Define_01()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            #if MY_DEFINE
            Console.WriteLine("Test output");
            #endif
            """);

        new DotnetCommand(Log, "run", "Program.cs", "-p:DefineConstants=MY_DEFINE")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Test output");
    }

    [Fact]
    public void Define_02()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            #if !MY_DEFINE
            Console.WriteLine("Test output");
            #endif
            """);

        new DotnetCommand(Log, "run", "Program.cs", "-p:DefineConstants=MY_DEFINE")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdOutContaining("error CS5001:"); // Program does not contain a static 'Main' method suitable for an entry point
    }

    [Fact]
    public void PackageReference()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            #:package System.CommandLine@2.0.0-beta4.22272.1
            using System.CommandLine;

            var rootCommand = new RootCommand("Sample app for System.CommandLine");
            return await rootCommand.InvokeAsync(args);
            """);

        new DotnetCommand(Log, "run", "Program.cs", "--", "--help")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOutContaining("""
                Description:
                  Sample app for System.CommandLine
                """);
    }

    [Fact]
    public void PackageReference_CentralVersion()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Directory.Packages.props"), """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="System.CommandLine" Version="2.0.0-beta4.22272.1" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            #:package System.CommandLine
            using System.CommandLine;

            var rootCommand = new RootCommand("Sample app for System.CommandLine");
            return await rootCommand.InvokeAsync(args);
            """);

        new DotnetCommand(Log, "run", "Program.cs", "--", "--help")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOutContaining("""
                Description:
                  Sample app for System.CommandLine
                """);
    }

    [Fact] // https://github.com/dotnet/sdk/issues/48990
    public void SdkReference()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            #:sdk Microsoft.NET.Sdk
            #:sdk Aspire.AppHost.Sdk@9.2.1
            #:package Aspire.Hosting.AppHost@9.2.1

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        new DotnetCommand(Log, "build", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass();
    }

    [Theory]
    [InlineData("../Lib/Lib.csproj")]
    [InlineData("../Lib")]
    [InlineData(@"..\Lib\Lib.csproj")]
    [InlineData(@"..\Lib")]
    public void ProjectReference(string arg)
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();

        var libDir = Path.Join(testInstance.Path, "Lib");
        Directory.CreateDirectory(libDir);

        File.WriteAllText(Path.Join(libDir, "Lib.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{ToolsetInfo.CurrentTargetFramework}</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Join(libDir, "Lib.cs"), """
            namespace Lib;
            public class LibClass
            {
                public static string GetMessage() => "Hello from Lib";
            }
            """);

        var appDir = Path.Join(testInstance.Path, "App");
        Directory.CreateDirectory(appDir);

        File.WriteAllText(Path.Join(appDir, "Program.cs"), $"""
            #:project {arg}
            Console.WriteLine(Lib.LibClass.GetMessage());
            """);

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(appDir)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Lib");
    }

    [Fact]
    public void ProjectReference_Errors()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            #:project wrong.csproj
            """);

        // Project file does not exist.
        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(CliCommandStrings.InvalidProjectDirective,
                $"{Path.Join(testInstance.Path, "Program.cs")}:1",
                string.Format(CliStrings.CouldNotFindProjectOrDirectory, Path.Join(testInstance.Path, "wrong.csproj"))));

        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            #:project dir/
            """);

        // Project directory does not exist.
        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(CliCommandStrings.InvalidProjectDirective,
                $"{Path.Join(testInstance.Path, "Program.cs")}:1",
                string.Format(CliStrings.CouldNotFindProjectOrDirectory, Path.Join(testInstance.Path, "dir/"))));

        Directory.CreateDirectory(Path.Join(testInstance.Path, "dir"));

        // Directory exists but has no project file.
        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(CliCommandStrings.InvalidProjectDirective,
                $"{Path.Join(testInstance.Path, "Program.cs")}:1",
                string.Format(CliStrings.CouldNotFindAnyProjectInDirectory, Path.Join(testInstance.Path, "dir/"))));

        File.WriteAllText(Path.Join(testInstance.Path, "dir", "proj1.csproj"), "<Project />");
        File.WriteAllText(Path.Join(testInstance.Path, "dir", "proj2.csproj"), "<Project />");

        // Directory exists but has multiple project files.
        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(CliCommandStrings.InvalidProjectDirective,
                $"{Path.Join(testInstance.Path, "Program.cs")}:1",
                string.Format(CliStrings.MoreThanOneProjectInDirectory, Path.Join(testInstance.Path, "dir/"))));
    }

    /// <summary>
    /// Verifies that arguments provided to <c>csc.exe</c> are the same as
    /// <c>dotnet build</c> would use for a simple <c>dotnet new console</c>.
    /// </summary>
    [Fact]
    public void CscArguments()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();

        // Create a project-based program.
        new DotnetCommand(Log, "new", "console")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass();

        // Build the project based program.
        new DotnetCommand(Log, "build", "-bl", "-p:UseArtifactsOutput=true")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass();

        // Find the csc args used by the build.
        var projectBasedCall = findCompilerCall(Path.Join(testInstance.Path, "msbuild.binlog"));
        var projectBasedCallArgs = projectBasedCall.GetArguments();
        var projectBasedCallArgsString = ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart(projectBasedCallArgs);

        const string artifactsPath = $"artifacts/obj/{nameof(CscArguments)}";

        // Print code that can be copy-pasted to fix up the arguments.
        var actualArgs = new List<string>();
        Log.WriteLine("Arguments should be:");
        Log.WriteLine("[");
        foreach (var arg in projectBasedCallArgs)
        {
            // We don't need to generate a ref assembly.
            if (arg.StartsWith("/refout", StringComparison.Ordinal))
            {
                continue;
            }

            // Ignore source link.
            if (arg.StartsWith("/sourcelink", StringComparison.Ordinal))
            {
                continue;
            }

            bool needsInterpolation = false;

            // Normalize slashes in paths.
            string rewritten = arg.Replace('\\', '/');

            // Remove quotes.
            rewritten = rewritten.Replace("\"", "");

            string actual = rewritten;

            // Use variable SDK path.
            string sdkPath = CSharpCompilerCommand.s_sdkPath.Replace('\\', '/');
            if (rewritten.Contains(sdkPath, StringComparison.OrdinalIgnoreCase))
            {
                rewritten = rewritten.Replace(sdkPath, "{" + nameof(CSharpCompilerCommand.s_sdkPath) + "}", StringComparison.OrdinalIgnoreCase);
                needsInterpolation = true;
            }

            // Use variable intermediate dir path.
            const string objPath = $"{artifactsPath}/debug";
            if (rewritten.Contains(objPath, StringComparison.OrdinalIgnoreCase))
            {
                rewritten = rewritten.Replace(objPath, "{intermediateDir}", StringComparison.OrdinalIgnoreCase);
                needsInterpolation = true;
            }

            // Use variable program name.
            if (rewritten.Contains(nameof(CscArguments), StringComparison.OrdinalIgnoreCase))
            {
                rewritten = rewritten.Replace(nameof(CscArguments), "{fileNameWithoutExtension}", StringComparison.OrdinalIgnoreCase);
                needsInterpolation = true;
            }

            // Use variable file name.
            const string fileName = "Program.cs";
            if (rewritten.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                rewritten = rewritten.Replace(fileName, "{" + nameof(CSharpCompilerCommand.EntryPointFileFullPath) + "}", StringComparison.OrdinalIgnoreCase);
                needsInterpolation = true;
            }

            // Ignore `/analyzerconfig` which is not variable (so it comes from the machine or sdk repo).
            if (!needsInterpolation && arg.StartsWith("/analyzerconfig", StringComparison.Ordinal))
            {
                continue;
            }

            string prefix = needsInterpolation ? "$" : string.Empty;

            Log.WriteLine($"""
                {prefix}"{rewritten}",
                """);

            actualArgs.Add(actual);
        }
        Log.WriteLine("]");

        // Re-create a file-based program in the same directory.
        //Directory.Delete(testInstance.Path, recursive: true);
        //Directory.CreateDirectory(testInstance.Path);
        //File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
        //    Console.WriteLine("Test");
        //    """);

        // Construct csc args for a file-based program.
        var fileBasedCall = new CSharpCompilerCommand()
        {
            EntryPointFileFullPath = Path.Join(testInstance.Path, "Program.cs"),
            ArtifactsPath = artifactsPath,
        };
        var fileBasedCallArgs = fileBasedCall.CreateArguments();
        var fileBasedCallArgsString = ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart(fileBasedCallArgs);

        // Check that project-based and file-based csc args are equivalent.
        var normalizedFileBasedArgs = fileBasedCallArgs.Select(a => a.Replace('\\', '/'));
        Log.WriteLine("File-based vs project-based args:" + Environment.NewLine +
            string.Join(Environment.NewLine, normalizedFileBasedArgs
                .Zip(actualArgs)
                .Select(p => $"{p.First}\t{p.Second}")));
        normalizedFileBasedArgs.Should().BeEquivalentTo(actualArgs);

        static CompilerCall findCompilerCall(string binaryLogPath)
        {
            using var reader = BinaryLogReader.Create(binaryLogPath);
            return reader.ReadAllCompilerCalls().Should().ContainSingle().Subject;
        }
    }

    [Fact]
    public void UpToDate()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory(baseDirectory: s_outOfTreeBaseDirectory);
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            Console.WriteLine("Hello v1");
            """);

        // Remove artifacts from possible previous runs of this test.
        var artifactsDir = VirtualProjectBuildingCommand.GetArtifactsPath(Path.Join(testInstance.Path, "Program.cs"));
        if (Directory.Exists(artifactsDir)) Directory.Delete(artifactsDir, recursive: true);

        Build(testInstance, BuildLevel.Csc, expectedOutput: "Hello v1");

        Build(testInstance, BuildLevel.None, expectedOutput: "Hello v1");

        Build(testInstance, BuildLevel.None, expectedOutput: "Hello v1");

        // Change the source file (a rebuild is necessary).
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        Build(testInstance, BuildLevel.Csc);

        Build(testInstance, BuildLevel.None);

        // Change an unrelated source file (no rebuild necessary).
        File.WriteAllText(Path.Join(testInstance.Path, "Program2.cs"), "test");

        Build(testInstance, BuildLevel.None);

        // Add an implicit build file (a rebuild is necessary).
        string buildPropsFile = Path.Join(testInstance.Path, "Directory.Build.props");
        File.WriteAllText(buildPropsFile, """
            <Project>
                <PropertyGroup>
                    <DefineConstants>$(DefineConstants);CUSTOM_DEFINE</DefineConstants>
                </PropertyGroup>
            </Project>
            """);

        Build(testInstance, BuildLevel.All, expectedOutput: """
            Hello from Program
            Custom define
            """);

        Build(testInstance, BuildLevel.None, expectedOutput: """
            Hello from Program
            Custom define
            """);

        // Change the implicit build file (a rebuild is necessary).
        string importedFile = Path.Join(testInstance.Path, "Settings.props");
        File.WriteAllText(importedFile, """
            <Project>
            </Project>
            """);
        File.WriteAllText(buildPropsFile, """
            <Project>
                <Import Project="Settings.props" />
            </Project>
            """);

        Build(testInstance, BuildLevel.All);

        // Change the imported build file (this is not recognized).
        File.WriteAllText(importedFile, """
            <Project>
                <PropertyGroup>
                    <DefineConstants>$(DefineConstants);CUSTOM_DEFINE</DefineConstants>
                </PropertyGroup>
            </Project>
            """);

        Build(testInstance, BuildLevel.None);

        // Force rebuild.
        Build(testInstance, BuildLevel.All, args: ["--no-cache"], expectedOutput: """
            Hello from Program
            Custom define
            """);

        // Remove an implicit build file (a rebuild is necessary).
        File.Delete(buildPropsFile);
        Build(testInstance, BuildLevel.Csc);

        // Force rebuild.
        Build(testInstance, BuildLevel.All, args: ["--no-cache"]);

        Build(testInstance, BuildLevel.None);

        // Pass argument (no rebuild necessary).
        Build(testInstance, BuildLevel.None, args: ["--", "test-arg"], expectedOutput: """
            echo args:test-arg
            Hello from Program
            """);

        // Change config (a rebuild is necessary).
        Build(testInstance, BuildLevel.All, args: ["-c", "Release"], expectedOutput: """
            Hello from Program
            Release config
            """);

        // Keep changed config (no rebuild necessary).
        Build(testInstance, BuildLevel.None, args: ["-c", "Release"], expectedOutput: """
            Hello from Program
            Release config
            """);

        // Change config back (a rebuild is necessary).
        Build(testInstance, BuildLevel.Csc);

        // Build with a failure.
        new DotnetCommand(Log, ["run", "Program.cs", "-p:LangVersion=Invalid"])
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdOutContaining("error CS1617"); // Invalid option 'Invalid' for /langversion.

        // A rebuild is necessary since the last build failed.
        Build(testInstance, BuildLevel.Csc);
    }

    private void Build(TestDirectory testInstance, BuildLevel level, ReadOnlySpan<string> args = default, string expectedOutput = "Hello from Program")
    {
        string prefix = level switch
        {
            BuildLevel.None => CliCommandStrings.NoBinaryLogBecauseUpToDate + Environment.NewLine,
            BuildLevel.Csc => CliCommandStrings.NoBinaryLogBecauseRunningJustCsc + Environment.NewLine,
            BuildLevel.All => string.Empty,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(level)),
        };

        new DotnetCommand(Log, ["run", "Program.cs", "-bl", .. args])
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut(prefix + expectedOutput);

        var binlogs = new DirectoryInfo(testInstance.Path)
            .EnumerateFiles("*.binlog", SearchOption.TopDirectoryOnly);

        binlogs.Select(f => f.Name)
            .Should().BeEquivalentTo(
                level switch
                {
                    BuildLevel.Csc => [],
                    BuildLevel.None => ["msbuild-dotnet-run.binlog"],
                    BuildLevel.All => ["msbuild.binlog", "msbuild-dotnet-run.binlog"],
                    _ => throw new ArgumentOutOfRangeException(paramName: nameof(level), message: level.ToString()),
                });

        foreach (var binlog in binlogs)
        {
            binlog.Delete();
        }
    }

    [Fact]
    public void UpToDate_InvalidOptions()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        new DotnetCommand(Log, "run", "Program.cs", "--no-cache", "--no-build")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(CliCommandStrings.InvalidOptionCombination, RunCommandParser.NoCacheOption.Name, RunCommandParser.NoBuildOption.Name));
    }

    [Fact]
    public void CscOnly()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory(baseDirectory: s_outOfTreeBaseDirectory);

        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            Console.WriteLine("v1");
            """);

        // Remove artifacts from possible previous runs of this test.
        var artifactsDir = VirtualProjectBuildingCommand.GetArtifactsPath(Path.Join(testInstance.Path, "Program.cs"));
        if (Directory.Exists(artifactsDir)) Directory.Delete(artifactsDir, recursive: true);

        Build(testInstance, BuildLevel.Csc, expectedOutput: "v1");

        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            Console.WriteLine("v2");
            #if !DEBUG
            Console.WriteLine("Release config");
            #endif
            """);

        Build(testInstance, BuildLevel.Csc, expectedOutput: "v2");

        // Customizing a property forces MSBuild to be used.
        Build(testInstance, BuildLevel.All, args: ["-c", "Release"], expectedOutput: """
            v2
            Release config
            """);
    }

    [Fact]
    public void CscOnly_CompilationDiagnostics()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory(baseDirectory: s_outOfTreeBaseDirectory);

        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            string x = null;
            Console.WriteLine("ran" + x);
            """);

        new DotnetCommand(Log, "run", "Program.cs", "-bl")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOutContaining(CliCommandStrings.NoBinaryLogBecauseRunningJustCsc)
            // warning CS8600: Converting null literal or possible null value to non-nullable type.
            .And.HaveStdOutContaining("warning CS8600")
            .And.HaveStdOutContaining("ran");

        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            Console.Write
            """);

        new DotnetCommand(Log, "run", "Program.cs", "-bl")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdOutContaining(CliCommandStrings.NoBinaryLogBecauseRunningJustCsc)
            // error CS1002: ; expected
            .And.HaveStdOutContaining("error CS1002")
            .And.HaveStdErrContaining(CliCommandStrings.RunCommandException);
    }

    /// <summary>
    /// Checks that the <c>DOTNET_ROOT</c> env var is set the same in csc mode as in msbuild mode.
    /// </summary>
    [Fact]
    public void CscOnly_DotNetRoot()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory(baseDirectory: s_outOfTreeBaseDirectory);
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            foreach (var entry in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process)
                .Cast<System.Collections.DictionaryEntry>()
                .Where(e => ((string)e.Key).StartsWith("DOTNET_ROOT")))
            {
                Console.WriteLine($"{entry.Key}={entry.Value}");
            }
            """);

        var expectedDotNetRoot = TestContext.Current.ToolsetUnderTest.DotNetRoot;

        var cscResult = new DotnetCommand(Log, "run", "Program.cs", "-bl")
            .WithWorkingDirectory(testInstance.Path)
            .Execute();

        cscResult.Should().Pass()
            .And.HaveStdOutContaining(CliCommandStrings.NoBinaryLogBecauseRunningJustCsc)
            .And.HaveStdOutContaining("DOTNET_ROOT");

        // Add an implicit build file to force use of msbuild instead of csc.
        File.WriteAllText(Path.Join(testInstance.Path, "Directory.Build.props"), "<Project />");

        var msbuildResult = new DotnetCommand(Log, "run", "Program.cs", "-bl")
            .WithWorkingDirectory(testInstance.Path)
            .Execute();

        msbuildResult.Should().Pass()
            .And.NotHaveStdOutContaining(CliCommandStrings.NoBinaryLogBecauseRunningJustCsc)
            .And.HaveStdOutContaining("DOTNET_ROOT");

        // The set of DOTNET_ROOT env vars should be the same in both cases.
        var cscVars = cscResult.StdOut!
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("DOTNET_ROOT"));
        var msbuildVars = msbuildResult.StdOut!
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("DOTNET_ROOT"));
        cscVars.Should().BeEquivalentTo(msbuildVars);
    }

    private static string ToJson(string s) => JsonSerializer.Serialize(s);

    /// <summary>
    /// Simplifies using interpolated raw strings with nested JSON,
    /// e.g, in <c>$$"""{x:{y:1}}"""</c>, the <c>}}</c> would result in an error.
    /// </summary>
    private const string nop = "";

    [Fact]
    public void Api()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var programPath = Path.Join(testInstance.Path, "Program.cs");
        File.WriteAllText(programPath, """
            #!/program
            #:sdk Microsoft.NET.Sdk
            #:sdk Aspire.Hosting.Sdk@9.1.0
            #:property TargetFramework=net11.0
            #:package System.CommandLine@2.0.0-beta4.22272.1
            #:property LangVersion=preview
            Console.WriteLine();
            """);

        new DotnetCommand(Log, "run-api")
            .WithStandardInput($$"""
                {"$type":"GetProject","EntryPointFileFullPath":{{ToJson(programPath)}},"ArtifactsPath":"/artifacts"}
                """)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut($$"""
                {"$type":"Project","Version":1,"Content":{{ToJson($"""
                    <Project>

                      <PropertyGroup>
                        <IncludeProjectNameInArtifactsPaths>false</IncludeProjectNameInArtifactsPaths>
                        <ArtifactsPath>/artifacts</ArtifactsPath>
                      </PropertyGroup>

                      <!-- We need to explicitly import Sdk props/targets so we can override the targets below. -->
                      <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
                      <Import Project="Sdk.props" Sdk="Aspire.Hosting.Sdk" Version="9.1.0" />

                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>{ToolsetInfo.CurrentTargetFramework}</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                        <PublishAot>true</PublishAot>
                      </PropertyGroup>

                      <PropertyGroup>
                        <EnableDefaultItems>false</EnableDefaultItems>
                      </PropertyGroup>

                      <PropertyGroup>
                        <TargetFramework>net11.0</TargetFramework>
                        <LangVersion>preview</LangVersion>
                      </PropertyGroup>

                      <PropertyGroup>
                        <Features>$(Features);FileBasedProgram</Features>
                      </PropertyGroup>

                      <ItemGroup>
                        <PackageReference Include="System.CommandLine" Version="2.0.0-beta4.22272.1" />
                      </ItemGroup>

                      <ItemGroup>
                        <Compile Include="{programPath}" />
                      </ItemGroup>

                      <ItemGroup>
                        <RuntimeHostConfigurationOption Include="EntryPointFilePath" Value="{programPath}" />
                        <RuntimeHostConfigurationOption Include="EntryPointFileDirectoryPath" Value="{testInstance.Path}" />
                      </ItemGroup>

                      <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
                      <Import Project="Sdk.targets" Sdk="Aspire.Hosting.Sdk" Version="9.1.0" />

                    {VirtualProjectBuildingCommand.TargetOverrides}

                    </Project>

                    """)}},"Diagnostics":[]}
                """);
    }

    [Fact]
    public void Api_Diagnostic_01()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var programPath = Path.Join(testInstance.Path, "Program.cs");
        File.WriteAllText(programPath, """
            Console.WriteLine();
            #:property LangVersion=preview
            """);

        new DotnetCommand(Log, "run-api")
            .WithStandardInput($$"""
                {"$type":"GetProject","EntryPointFileFullPath":{{ToJson(programPath)}},"ArtifactsPath":"/artifacts"}
                """)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut($$"""
                {"$type":"Project","Version":1,"Content":{{ToJson($"""
                    <Project>

                      <PropertyGroup>
                        <IncludeProjectNameInArtifactsPaths>false</IncludeProjectNameInArtifactsPaths>
                        <ArtifactsPath>/artifacts</ArtifactsPath>
                      </PropertyGroup>

                      <!-- We need to explicitly import Sdk props/targets so we can override the targets below. -->
                      <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />

                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>{ToolsetInfo.CurrentTargetFramework}</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                        <PublishAot>true</PublishAot>
                      </PropertyGroup>

                      <PropertyGroup>
                        <EnableDefaultItems>false</EnableDefaultItems>
                      </PropertyGroup>

                      <PropertyGroup>
                        <Features>$(Features);FileBasedProgram</Features>
                      </PropertyGroup>

                      <ItemGroup>
                        <Compile Include="{programPath}" />
                      </ItemGroup>

                      <ItemGroup>
                        <RuntimeHostConfigurationOption Include="EntryPointFilePath" Value="{programPath}" />
                        <RuntimeHostConfigurationOption Include="EntryPointFileDirectoryPath" Value="{testInstance.Path}" />
                      </ItemGroup>

                      <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />

                    {VirtualProjectBuildingCommand.TargetOverrides}

                    </Project>

                    """)}},"Diagnostics":
                [{"Location":{
                "Path":{{ToJson(programPath)}},
                "Span":{"Start":{"Line":1,"Character":0},"End":{"Line":1,"Character":30}{{nop}}}{{nop}}},
                "Message":{{ToJson(string.Format(CliCommandStrings.CannotConvertDirective, $"{programPath}:2"))}}}]}
                """.ReplaceLineEndings(""));
    }

    [Fact]
    public void Api_Diagnostic_02()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var programPath = Path.Join(testInstance.Path, "Program.cs");
        File.WriteAllText(programPath, """
            #:unknown directive
            Console.WriteLine();
            """);

        new DotnetCommand(Log, "run-api")
            .WithStandardInput($$"""
                {"$type":"GetProject","EntryPointFileFullPath":{{ToJson(programPath)}},"ArtifactsPath":"/artifacts"}
                """)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut($$"""
                {"$type":"Project","Version":1,"Content":{{ToJson($"""
                    <Project>

                      <PropertyGroup>
                        <IncludeProjectNameInArtifactsPaths>false</IncludeProjectNameInArtifactsPaths>
                        <ArtifactsPath>/artifacts</ArtifactsPath>
                      </PropertyGroup>

                      <!-- We need to explicitly import Sdk props/targets so we can override the targets below. -->
                      <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />

                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>{ToolsetInfo.CurrentTargetFramework}</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
                        <PublishAot>true</PublishAot>
                      </PropertyGroup>

                      <PropertyGroup>
                        <EnableDefaultItems>false</EnableDefaultItems>
                      </PropertyGroup>

                      <PropertyGroup>
                        <Features>$(Features);FileBasedProgram</Features>
                      </PropertyGroup>

                      <ItemGroup>
                        <Compile Include="{programPath}" />
                      </ItemGroup>

                      <ItemGroup>
                        <RuntimeHostConfigurationOption Include="EntryPointFilePath" Value="{programPath}" />
                        <RuntimeHostConfigurationOption Include="EntryPointFileDirectoryPath" Value="{testInstance.Path}" />
                      </ItemGroup>

                      <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />

                    {VirtualProjectBuildingCommand.TargetOverrides}

                    </Project>

                    """)}},"Diagnostics":
                [{"Location":{
                "Path":{{ToJson(programPath)}},
                "Span":{"Start":{"Line":0,"Character":0},"End":{"Line":1,"Character":0}{{nop}}}{{nop}}},
                "Message":{{ToJson(string.Format(CliCommandStrings.UnrecognizedDirective, "unknown", $"{programPath}:1"))}}}]}
                """.ReplaceLineEndings(""));
    }

    [Fact]
    public void Api_Error()
    {
        new DotnetCommand(Log, "run-api")
            .WithStandardInput("""
                {"$type":"Unknown1"}
                {"$type":"Unknown2"}
                """)
            .Execute()
            .Should().Pass()
            .And.HaveStdOutContaining("""
                {"$type":"Error","Version":1,"Message":
                """)
            .And.HaveStdOutContaining("Unknown1")
            .And.HaveStdOutContaining("Unknown2");
    }

    [Fact]
    public void Api_RunCommand()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var programPath = Path.Join(testInstance.Path, "Program.cs");
        File.WriteAllText(programPath, """
            Console.WriteLine();
            """);

        string artifactsPath = OperatingSystem.IsWindows() ? @"C:\artifacts" : "/artifacts";
        string executablePath = OperatingSystem.IsWindows() ? @"C:\artifacts\bin\debug\Program.exe" : "/artifacts/bin/debug/Program";
        new DotnetCommand(Log, "run-api")
            .WithStandardInput($$"""
                {"$type":"GetRunCommand","EntryPointFileFullPath":{{ToJson(programPath)}},"ArtifactsPath":{{ToJson(artifactsPath)}}}
                """)
            .Execute()
            .Should().Pass()
            // DOTNET_ROOT environment variable is platform dependent so we don't verify it fully for simplicity
            .And.HaveStdOutContaining($$"""
                {"$type":"RunCommand","Version":1,"ExecutablePath":{{ToJson(executablePath)}},"CommandLineArguments":"","WorkingDirectory":"","EnvironmentVariables":{"DOTNET_ROOT
                """);
    }

    [Fact]
    public void EntryPointFilePath()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var filePath = Path.Join(testInstance.Path, "Program.cs");
        File.WriteAllText(filePath, """"
            var entryPointFilePath = AppContext.GetData("EntryPointFilePath") as string;
            Console.WriteLine($"""EntryPointFilePath: {entryPointFilePath}""");
            """");

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut($"EntryPointFilePath: {filePath}");
    }

    [Fact]
    public void EntryPointFileDirectoryPath()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """"
            var entryPointFileDirectoryPath = AppContext.GetData("EntryPointFileDirectoryPath") as string;
            Console.WriteLine($"""EntryPointFileDirectoryPath: {entryPointFileDirectoryPath}""");
            """");

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut($"EntryPointFileDirectoryPath: {testInstance.Path}");
    }

    [Fact]
    public void EntryPointFilePath_WithRelativePath()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var fileName = "Program.cs";
        File.WriteAllText(Path.Join(testInstance.Path, fileName), """
            var entryPointFilePath = AppContext.GetData("EntryPointFilePath") as string;
            Console.WriteLine($"EntryPointFilePath: {entryPointFilePath}");
            """);

        var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.Join(testInstance.Path, fileName));
        new DotnetCommand(Log, "run", relativePath)
            .WithWorkingDirectory(Directory.GetCurrentDirectory())
            .Execute()
            .Should().Pass()
            .And.HaveStdOut($"EntryPointFilePath: {Path.GetFullPath(relativePath)}");
    }

    [Fact]
    public void EntryPointFilePath_WithSpacesInPath()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var dirWithSpaces = Path.Join(testInstance.Path, "dir with spaces");
        Directory.CreateDirectory(dirWithSpaces);
        var filePath = Path.Join(dirWithSpaces, "Program.cs");
        File.WriteAllText(filePath, """
        var entryPointFilePath = AppContext.GetData("EntryPointFilePath") as string;
        Console.WriteLine($"EntryPointFilePath: {entryPointFilePath}");
        """);

        new DotnetCommand(Log, "run", filePath)
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut($"EntryPointFilePath: {filePath}");
    }

    [Fact]
    public void EntryPointFileDirectoryPath_WithDotSlash()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var fileName = "Program.cs";
        File.WriteAllText(Path.Join(testInstance.Path, fileName), """
        var entryPointFileDirectoryPath = AppContext.GetData("EntryPointFileDirectoryPath") as string;
        Console.WriteLine($"EntryPointFileDirectoryPath: {entryPointFileDirectoryPath}");
        """);

        new DotnetCommand(Log, "run", $"./{fileName}")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut($"EntryPointFileDirectoryPath: {testInstance.Path}");
    }

    [Fact]
    public void EntryPointFilePath_WithUnicodeCharacters()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        var unicodeFileName = "Программа.cs";
        var filePath = Path.Join(testInstance.Path, unicodeFileName);
        File.WriteAllText(filePath, """
        var entryPointFilePath = AppContext.GetData("EntryPointFilePath") as string;
        Console.WriteLine($"EntryPointFilePath: {entryPointFilePath}");
        """);

        new DotnetCommand(Log, "run", unicodeFileName)
            .WithWorkingDirectory(testInstance.Path)
            .WithStandardOutputEncoding(Encoding.UTF8)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut($"EntryPointFilePath: {filePath}");
    }
}
