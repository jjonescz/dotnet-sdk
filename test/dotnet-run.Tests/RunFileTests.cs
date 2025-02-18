// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.Tools.Run;

namespace Microsoft.DotNet.Cli.Run.Tests;

public sealed class RunFileTests(ITestOutputHelper log) : SdkTest(log)
{
    private static readonly string s_program = """
        if (args.Length > 0)
        {
            Console.WriteLine("echo args:" + string.Join(";", args));
        }
        Console.WriteLine("Hello from " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Name);
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

    private static readonly string s_consoleProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>$(CurrentTargetFramework)</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private static readonly string s_runCommandExceptionNoProjects =
        "Couldn't find a project to run.";

    private static readonly string s_noTopLevelStatements =
        "Cannot run a file without top-level statements and without a project:";

    /// <summary>
    /// <c>dotnet run file.cs</c> -> ok
    /// </summary>
    [Theory]
    [InlineData(null)] // will be replaced with an absolute path
    [InlineData("Program.cs")]
    [InlineData("./Program.cs")]
    [InlineData("Program.CS")]
    public void FilePath(string? path)
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();

        var programPath = Path.Join(testInstance.Path, "Program.cs");

        File.WriteAllText(programPath, s_program);

        path ??= programPath;

        new DotnetCommand(Log, "run", path)
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from Program");
    }

    /// <summary>
    /// Casing of the argument is used for the final binary.
    /// </summary>
    [Fact]
    public void FilePath_DifferentCasing()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);
        new DotnetCommand(Log, "run", "program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello from program");
    }

    /// <summary>
    /// <c>dotnet run folder/file.cs</c> -> ok
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
    /// <c>dotnet run --project file.cs</c> -> fails
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
            .And.HaveStdErrContaining(LocalizableStrings.RunCommandException);
    }

    /// <summary>
    /// <c>dotnet run folder</c> -> not supported
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
            .And.HaveStdErrContaining(s_runCommandExceptionNoProjects);
    }

    /// <summary>
    /// <c>dotnet run app.csproj</c> where app.csproj does not exist -> fails
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
            .And.HaveStdErrContaining(s_runCommandExceptionNoProjects);
    }

    /// <summary>
    /// <c>dotnet run app.csproj</c> where app.csproj exists
    /// -> runs the project and passes 'app.csproj' as an argument
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
            .And.HaveStdOut("""
                echo args:./App.csproj
                Hello from Program
                """);
    }

    /// <summary>
    /// Only <c>.cs</c> files can be recognized as entry-point files,
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
            .And.HaveStdErrContaining(s_runCommandExceptionNoProjects);
    }

    /// <summary>
    /// Command-line arguments should be passed through.
    /// </summary>
    [Fact]
    public void Arguments()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);

        new DotnetCommand(Log, "run", "Program.cs", "other", "args")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("""
                echo args:other;args
                Hello from Program
                """);
    }

    /// <summary>
    /// The build fails when there are multiple files with entry points.
    /// </summary>
    [Fact]
    public void MultipleEntryPoints()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);
        File.WriteAllText(Path.Join(testInstance.Path, "Program2.cs"), s_program);

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(LocalizableStrings.RunCommandException);
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
            .And.HaveStdErrContaining(s_runCommandExceptionNoProjects);
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
            .And.HaveStdErrContaining(s_noTopLevelStatements);
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
            .And.HaveStdErrContaining(s_runCommandExceptionNoProjects);
    }

    /// <summary>
    /// Other files in the folder are part of the compilation.
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
            .Should().Pass()
            .And.HaveStdOut("Hello, String from Util");
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
            .And.HaveStdErrContaining(s_noTopLevelStatements);
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
    /// However, same problem exists for normal builds with explicit project files.
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
    /// (it does not have <c>.cs</c> file extension), so this fall backs to normal <c>dotnet run</c> behavior.
    /// </summary>
    [Fact]
    public void RunNestedProjectFile()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), s_program);
        File.WriteAllText(Path.Join(testInstance.Path, "App.csproj"), s_consoleProject);

        var dirName = Path.GetFileName(testInstance.Path);

        new DotnetCommand(Log, "run", $"{dirName}/App.csproj")
            .WithWorkingDirectory(Path.GetDirectoryName(testInstance.Path)!)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(s_runCommandExceptionNoProjects);
    }

    /// <summary>
    /// Only top-level statements are supported for now; Main method is not.
    /// </summary>
    [Fact]
    public void MainMethod()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp").WithSource();
        File.Delete(Path.Join(testInstance.Path, "MSBuildTestApp.csproj"));

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(s_noTopLevelStatements);
    }

    /// <summary>
    /// Empty file does not contain top-level statements, so that's an error.
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
            .And.HaveStdErrContaining(s_noTopLevelStatements);
    }
}
