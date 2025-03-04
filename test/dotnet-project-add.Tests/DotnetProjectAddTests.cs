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

        var dotnetNewConsole = Path.Join(testInstance.Path, "DotnetNewConsole");
        Directory.CreateDirectory(dotnetNewConsole);

        new DotnetCommand(Log, "new", "console")
            .WithWorkingDirectory(dotnetNewConsole)
            .Execute()
            .Should().Pass();

        var projectFile1 = File.ReadAllText(Directory.EnumerateFiles(dotnetProjectAdd, "*.csproj").Single());
        var projectFile2 = File.ReadAllText(Directory.EnumerateFiles(dotnetNewConsole, "*.csproj").Single());
        projectFile1.Should().Be(projectFile2)
            .And.StartWith("""<Project Sdk="Microsoft.NET.Sdk">""");
    }
}
