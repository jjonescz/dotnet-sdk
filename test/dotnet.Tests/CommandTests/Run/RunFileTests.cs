// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Basic.CompilerLog.Util;
using System.Text.Json;
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
    /// Only <c>.cs</c> files can be run without a project file,
    /// others fall back to normal <c>dotnet run</c> behavior.
    /// </summary>
    [Theory]
    [InlineData("Program")]
    [InlineData("Program.csx")]
    [InlineData("Program.vb")]
    public void NonCsFileExtension(string fileName)
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
    public void NoRestore()
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
    public void LaunchProfile()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program + """

            Console.WriteLine($"Message: '{Environment.GetEnvironmentVariable("Message")}'");
            """);
        Directory.CreateDirectory(Path.Join(testInstance.Path, "Properties"));
        File.WriteAllText(Path.Join(testInstance.Path, "Properties", "launchSettings.json"), s_launchSettings);

        new DotnetCommand(Log, "run", "--no-launch-profile", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("""
                Hello from Program
                Message: ''
                """);

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOutContaining("""
                Hello from Program
                Message: 'TestProfileMessage1'
                """);

        new DotnetCommand(Log, "run", "-lp", "TestProfile2", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOutContaining("""
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
            #:package System.CommandLine 2.0.0-beta4.22272.1
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
        Log.WriteLine(nameof(CSharpCompilerCommand.s_cscPath) + ",");
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
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            Console.WriteLine("Hello v1");
            """);

        // Remove artifacts from possible previous runs of this test.
        var artifactsDir = VirtualProjectBuildingCommand.GetArtifactsPath(Path.Join(testInstance.Path, "Program.cs"));
        if (Directory.Exists(artifactsDir)) Directory.Delete(artifactsDir, recursive: true);

        Build(testInstance, BuildLevel.All, expectedOutput: "Hello v1");

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
        Build(testInstance, BuildLevel.All);

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
        Build(testInstance, BuildLevel.All);

        // Build with a failure.
        new DotnetCommand(Log, ["run", "Program.cs", "-p:LangVersion=Invalid"])
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdOutContaining("error CS1617"); // Invalid option 'Invalid' for /langversion.

        // A rebuild is necessary since the last build failed.
        Build(testInstance, BuildLevel.All);
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
                level != BuildLevel.All
                    ? ["msbuild-dotnet-run.binlog"]
                    : ["msbuild.binlog", "msbuild-dotnet-run.binlog"]);

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

        new DotnetCommand(Log, "run", "Program.cs", "--no-cache", "--no-restore")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(string.Format(CliCommandStrings.InvalidOptionCombination, RunCommandParser.NoCacheOption.Name, RunCommandParser.NoRestoreOption.Name));
    }

    [Fact]
    public void CscOnly()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            Console.WriteLine("v1");
            """);

        // Remove artifacts from possible previous runs of this test.
        var artifactsDir = VirtualProjectBuildingCommand.GetArtifactsPath(Path.Join(testInstance.Path, "Program.cs"));
        if (Directory.Exists(artifactsDir)) Directory.Delete(artifactsDir, recursive: true);

        // First is full build because cache file does not exist.
        // TODO: This should go away.
        Build(testInstance, BuildLevel.All, expectedOutput: "v1");

        Build(testInstance, BuildLevel.None, expectedOutput: "v1");

        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), """
            Console.WriteLine("v2");
            """);

        Build(testInstance, BuildLevel.Csc, expectedOutput: "v2");
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
            #:sdk Aspire.Hosting.Sdk 9.1.0
            #:property TargetFramework net11.0
            #:package System.CommandLine 2.0.0-beta4.22272.1
            #:property LangVersion preview
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
                      <Import Project="Sdk.props" Sdk="Aspire.Hosting.Sdk/9.1.0" />

                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>{ToolsetInfo.CurrentTargetFramework}</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <Nullable>enable</Nullable>
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

                      <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
                      <Import Project="Sdk.targets" Sdk="Aspire.Hosting.Sdk/9.1.0" />

                      <!--
                        Override targets which don't work with project files that are not present on disk.
                        See https://github.com/NuGet/Home/issues/14148.
                      -->

                      <Target Name="_FilterRestoreGraphProjectInputItems"
                              DependsOnTargets="_LoadRestoreGraphEntryPoints"
                              Returns="@(FilteredRestoreGraphProjectInputItems)">
                        <ItemGroup>
                          <FilteredRestoreGraphProjectInputItems Include="@(RestoreGraphProjectInputItems)" />
                        </ItemGroup>
                      </Target>

                      <Target Name="_GetAllRestoreProjectPathItems"
                              DependsOnTargets="_FilterRestoreGraphProjectInputItems"
                              Returns="@(_RestoreProjectPathItems)">
                        <ItemGroup>
                          <_RestoreProjectPathItems Include="@(FilteredRestoreGraphProjectInputItems)" />
                        </ItemGroup>
                      </Target>

                      <Target Name="_GenerateRestoreGraph"
                              DependsOnTargets="_FilterRestoreGraphProjectInputItems;_GetAllRestoreProjectPathItems;_GenerateRestoreGraphProjectEntry;_GenerateProjectRestoreGraph"
                              Returns="@(_RestoreGraphEntry)">
                        <!-- Output from dependency _GenerateRestoreGraphProjectEntry and _GenerateProjectRestoreGraph -->
                      </Target>

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
            #:property LangVersion preview
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

                      <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />

                      <!--
                        Override targets which don't work with project files that are not present on disk.
                        See https://github.com/NuGet/Home/issues/14148.
                      -->

                      <Target Name="_FilterRestoreGraphProjectInputItems"
                              DependsOnTargets="_LoadRestoreGraphEntryPoints"
                              Returns="@(FilteredRestoreGraphProjectInputItems)">
                        <ItemGroup>
                          <FilteredRestoreGraphProjectInputItems Include="@(RestoreGraphProjectInputItems)" />
                        </ItemGroup>
                      </Target>

                      <Target Name="_GetAllRestoreProjectPathItems"
                              DependsOnTargets="_FilterRestoreGraphProjectInputItems"
                              Returns="@(_RestoreProjectPathItems)">
                        <ItemGroup>
                          <_RestoreProjectPathItems Include="@(FilteredRestoreGraphProjectInputItems)" />
                        </ItemGroup>
                      </Target>

                      <Target Name="_GenerateRestoreGraph"
                              DependsOnTargets="_FilterRestoreGraphProjectInputItems;_GetAllRestoreProjectPathItems;_GenerateRestoreGraphProjectEntry;_GenerateProjectRestoreGraph"
                              Returns="@(_RestoreGraphEntry)">
                        <!-- Output from dependency _GenerateRestoreGraphProjectEntry and _GenerateProjectRestoreGraph -->
                      </Target>

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

                      <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />

                      <!--
                        Override targets which don't work with project files that are not present on disk.
                        See https://github.com/NuGet/Home/issues/14148.
                      -->

                      <Target Name="_FilterRestoreGraphProjectInputItems"
                              DependsOnTargets="_LoadRestoreGraphEntryPoints"
                              Returns="@(FilteredRestoreGraphProjectInputItems)">
                        <ItemGroup>
                          <FilteredRestoreGraphProjectInputItems Include="@(RestoreGraphProjectInputItems)" />
                        </ItemGroup>
                      </Target>

                      <Target Name="_GetAllRestoreProjectPathItems"
                              DependsOnTargets="_FilterRestoreGraphProjectInputItems"
                              Returns="@(_RestoreProjectPathItems)">
                        <ItemGroup>
                          <_RestoreProjectPathItems Include="@(FilteredRestoreGraphProjectInputItems)" />
                        </ItemGroup>
                      </Target>

                      <Target Name="_GenerateRestoreGraph"
                              DependsOnTargets="_FilterRestoreGraphProjectInputItems;_GetAllRestoreProjectPathItems;_GenerateRestoreGraphProjectEntry;_GenerateProjectRestoreGraph"
                              Returns="@(_RestoreGraphEntry)">
                        <!-- Output from dependency _GenerateRestoreGraphProjectEntry and _GenerateProjectRestoreGraph -->
                      </Target>

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
}
