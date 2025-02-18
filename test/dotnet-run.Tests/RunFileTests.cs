using Microsoft.Build.Tasks;
using Microsoft.DotNet.Tools.Run;

namespace Microsoft.DotNet.Cli.Run.Tests;

public sealed class RunFileTests(ITestOutputHelper log) : SdkTest(log)
{
    /// <summary>
    /// <c>dotnet run file.cs</c> -> ok
    /// </summary>
    [Theory]
    [InlineData(null)] // will be replaced with an absolute path
    [InlineData("Program.cs")]
    [InlineData("./Program.cs")]
    [InlineData("program.CS")]
    public void FilePath(string? path)
    {
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp")
            .WithSource()
            .RemoveProjectFiles();

        path ??= Path.Join(testInstance.Path, "Program.cs");

        new DotnetCommand(Log, "run", path)
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello World!");
    }

    /// <summary>
    /// <c>dotnet run folder/file.cs</c> -> ok
    /// </summary>
    [Fact]
    public void FilePath_OutsideWorkDir()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp")
            .WithSource()
            .RemoveProjectFiles();

        var dirName = Path.GetFileName(testInstance.Path);

        new DotnetCommand(Log, "run", $"{dirName}/Program.cs")
            .WithWorkingDirectory(Path.GetDirectoryName(testInstance.Path)!)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("Hello World!");
    }

    /// <summary>
    /// <c>dotnet run --project file.cs</c> -> fails
    /// </summary>
    [Fact]
    public void FilePath_AsProjectArgument()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp")
            .WithSource()
            .RemoveProjectFiles();

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
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp")
            .WithSource()
            .RemoveProjectFiles();

        path ??= testInstance.Path;

        new DotnetCommand(Log, "run", path)
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("Couldn't find a project to run.");
    }

    /// <summary>
    /// <c>dotnet run app.csproj</c> where app.csproj does not exist -> fails
    /// </summary>
    [Fact]
    public void ProjectPath_DoesNotExist()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp")
            .WithSource()
            .RemoveProjectFiles();

        new DotnetCommand(Log, "run", "./MSBuildTestApp.csproj")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("Couldn't find a project to run.");
    }

    /// <summary>
    /// <c>dotnet run app.csproj</c> where app.csproj exists
    /// -> runs the project and passes 'app.csproj' as an argument
    /// </summary>
    [Fact]
    public void ProjectPath_Exists()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp")
            .WithSource();

        new DotnetCommand(Log, "run", "./MSBuildTestApp.csproj")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("""
                echo args:./MSBuildTestApp.csproj
                Hello World!
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
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp")
            .WithSource()
            .RemoveProjectFiles();

        File.Move(Path.Join(testInstance.Path, "Program.cs"), Path.Join(testInstance.Path, fileName));

        new DotnetCommand(Log, "run", fileName)
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("Couldn't find a project to run.");
    }

    [Fact]
    public void Arguments()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp")
            .WithSource()
            .RemoveProjectFiles();

        new DotnetCommand(Log, "run", "Program.cs", "other", "args")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("""
                echo args:other;args
                Hello World!
                """);
    }

    [Fact]
    public void MultipleEntryPoints()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp")
            .WithSource()
            .RemoveProjectFiles();

        File.Copy(Path.Join(testInstance.Path, "Program.cs"), Path.Join(testInstance.Path, "Program2.cs"));

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(LocalizableStrings.RunCommandException);
    }

    [Fact]
    public void NoCode()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("EmptyFolder")
            .WithSource();

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("Couldn't find a project to run.");
    }

    [Fact]
    public void ClassLibrary_EntryPointFileExists()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("AppWithLibrary")
            .WithSource()
            .RemoveProjectFiles();

        new DotnetCommand(Log, "run", "Helper.cs")
            .WithWorkingDirectory(Path.Join(testInstance.Path, "TestLibrary"))
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining(LocalizableStrings.RunCommandException);
    }

    [Fact]
    public void ClassLibrary_EntryPointFileDoesNotExist()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("AppWithLibrary")
            .WithSource()
            .RemoveProjectFiles();

        new DotnetCommand(Log, "run", "NonExistentFile.cs")
            .WithWorkingDirectory(Path.Join(testInstance.Path, "TestLibrary"))
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("Couldn't find a project to run.");
    }

    [Fact]
    public void MultipleFiles_RunEntryPoint()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("AppWithLibrary")
            .WithSource()
            .RemoveProjectFiles();

        var appDirectory = Path.Join(testInstance.Path, "TestApp");

        // Copy .cs files into one folder.
        File.Copy(Path.Join(testInstance.Path, "TestLibrary/Helper.cs"), Path.Join(appDirectory, "Helper.cs"));

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(appDirectory)
            .Execute()
            .Should().Pass()
            .And.HaveStdOut("This string came from the test library!");
    }

    [Fact]
    public void MultipleFiles_RunLibraryFile()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("AppWithLibrary")
            .WithSource()
            .RemoveProjectFiles();

        var appDirectory = Path.Join(testInstance.Path, "TestApp");

        // Copy .cs files into one folder.
        File.Copy(Path.Join(testInstance.Path, "TestLibrary/Helper.cs"), Path.Join(appDirectory, "Helper.cs"));

        new DotnetCommand(Log, "run", "Helper.cs")
            .WithWorkingDirectory(appDirectory)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("TODO");
    }

    [Fact]
    public void NestedProjectFiles()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp")
            .WithSource();

        // Move .csproj into a nested folder.
        Directory.CreateDirectory(Path.Join(testInstance.Path, "nested"));
        File.Move(Path.Join(testInstance.Path, "MSBuildTestApp.csproj"), Path.Join(testInstance.Path, "nested", "MSBuildTestApp.csproj"));

        new DotnetCommand(Log, "run", "Program.cs")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("TODO");
    }

    /// <summary>
    /// <c>dotnet run folder/app.csproj</c> -> the argument is not recognized as an entry-point file
    /// (it does not have <c>.cs</c> file extension), so this fall backs to normal <c>dotnet run</c> behavior.
    /// </summary>
    [Fact]
    public void RunNestedProjectFile()
    {
        var testInstance = _testAssetsManager.CopyTestAsset("MSBuildTestApp")
            .WithSource();

        var dirName = Path.GetFileName(testInstance.Path);

        new DotnetCommand(Log, "run", $"{dirName}/MSBuildTestApp.csproj")
            .WithWorkingDirectory(Path.GetDirectoryName(testInstance.Path)!)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("Couldn't find a project to run.");
    }
}
