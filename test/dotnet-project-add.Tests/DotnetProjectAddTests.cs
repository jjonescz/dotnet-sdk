// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.DotNet.Cli.Project.Add.Tests;

public sealed class DotnetProjectAddTests(ITestOutputHelper log) : SdkTest(log)
{
    /// <summary>
    /// <c>dotnet project add</c> should result in the same project file text as <c>dotnet new console</c>.
    /// If this test fails, <c>dotnet project add</c> command implementation should be updated.
    /// </summary>
    [Fact]
    public void SameAsTemplate()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();

        var dotnetProjectAdd = Path.Join(testInstance.Path, "DotnetProjectAdd");
        Directory.CreateDirectory(dotnetProjectAdd);

        var csFile = Path.Combine(dotnetProjectAdd, "Program.cs");
        File.WriteAllText(csFile, """Console.WriteLine("Test");""");

        new DotnetCommand(Log, "project", "add")
            .WithWorkingDirectory(dotnetProjectAdd)
            .Execute()
            .Should().Pass();

        var dotnetProjectAddProject = Directory.EnumerateFiles(dotnetProjectAdd, "*.csproj").Single();

        Path.GetFileName(dotnetProjectAddProject).Should().Be("Program.csproj");

        var dotnetNewConsole = Path.Join(testInstance.Path, "DotnetNewConsole");
        Directory.CreateDirectory(dotnetNewConsole);

        new DotnetCommand(Log, "new", "console")
            .WithWorkingDirectory(dotnetNewConsole)
            .Execute()
            .Should().Pass();

        var dotnetNewConsoleProject = Directory.EnumerateFiles(dotnetNewConsole, "*.csproj").Single();

        var dotnetProjectAddProjectText = File.ReadAllText(dotnetProjectAddProject);
        var dotnetNewConsoleProjectText = File.ReadAllText(dotnetNewConsoleProject);
        dotnetProjectAddProjectText.Should().Be(dotnetNewConsoleProjectText)
            .And.StartWith("""<Project Sdk="Microsoft.NET.Sdk">""");
    }

    [Fact]
    public void ProjectFileAlreadyExists()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "App.csproj"), "");

        new DotnetCommand(Log, "project", "add")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("The target directory already contains a project file");
    }

    [Fact]
    public void MultipleEntryPointFiles()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program1.cs"), "_ = 0;");
        File.WriteAllText(Path.Join(testInstance.Path, "Program2.cs"), "_ = 0;");

        new DotnetCommand(Log, "project", "add")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("Multiple entry-point files");
    }

    [Fact]
    public void NoEntryPointFile()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();

        new DotnetCommand(Log, "project", "add")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("No entry-point C# file with top-level statements found in directory");
    }

    [Fact]
    public void NoTopLevelStatements()
    {
        var testInstance = _testAssetsManager.CreateTestDirectory();
        File.WriteAllText(Path.Join(testInstance.Path, "Program.cs"), "class C;");

        new DotnetCommand(Log, "project", "add")
            .WithWorkingDirectory(testInstance.Path)
            .Execute()
            .Should().Fail()
            .And.HaveStdErrContaining("No entry-point C# file with top-level statements found in directory");
    }
}
