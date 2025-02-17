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
    public void FilePath_OutsideWorkdir()
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

        var workingDirectory = testInstance.Path.TrimEnd('/', '\\');

        new DotnetCommand(Log, "run", "--project", "Program.cs")
            .WithWorkingDirectory(workingDirectory)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContainingOnce(LocalizableStrings.RunCommandException);
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

        var workingDirectory = testInstance.Path.TrimEnd('/', '\\');

        new DotnetCommand(Log, "run", path)
            .WithWorkingDirectory(workingDirectory)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContainingOnce(string.Format(LocalizableStrings.RunCommandExceptionNoProjects, workingDirectory, "--project"));
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

        var workingDirectory = testInstance.Path.TrimEnd('/', '\\');

        new DotnetCommand(Log, "run", "./MSBuildTestApp.csproj")
            .WithWorkingDirectory(workingDirectory)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContainingOnce(string.Format(LocalizableStrings.RunCommandExceptionNoProjects, workingDirectory, "--project"));
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
}
