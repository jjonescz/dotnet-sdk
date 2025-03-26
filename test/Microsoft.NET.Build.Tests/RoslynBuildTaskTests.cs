// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Basic.CompilerLog.Util;

namespace Microsoft.NET.Build.Tests;

public sealed class RoslynBuildTaskTests(ITestOutputHelper log) : SdkTest(log)
{
    [FullMSBuildOnlyFact]
    public void FullMSBuild_SdkStyle()
    {
        var testAsset = _testAssetsManager.CreateTestProject(new TestProject
        {
            Name = "App1",
            IsExe = true,
            SourceFiles =
            {
                ["Program.cs"] = """
                    System.Console.WriteLine(40 + 2);
                    """,
            },
        });
        var buildCommand = new MSBuildCommand(testAsset, "Build");
        buildCommand.WithWorkingDirectory(testAsset.Path)
            .Execute("-bl").Should().Pass();

        var runCommand = new RunExeCommand(Log, buildCommand.GetOutputFile().FullName);
        runCommand.Execute().Should().Pass()
            .And.HaveStdOut("42");

        using var reader = BinaryLogReader.Create(Path.Join(buildCommand.WorkingDirectory, "msbuild.binlog"));
        var call = reader.ReadAllCompilerCalls().Should().ContainSingle().Subject;
        Path.GetFileName(call.CompilerFilePath).Should().Be("csc.exe");
    }

    [Fact]
    public void DotNet()
    {
        var testAsset = _testAssetsManager.CreateTestProject(new TestProject
        {
            Name = "App1",
            IsExe = true,
            SourceFiles =
            {
                ["Program.cs"] = """
                    System.Console.WriteLine(40 + 2);
                    """,
            },
        });
        var buildCommand = new DotnetBuildCommand(testAsset);
        buildCommand.Execute("-bl").Should().Pass();

        var runCommand = new RunExeCommand(Log, buildCommand.GetOutputFile().FullName);
        runCommand.Execute().Should().Pass()
            .And.HaveStdOut("42");

        using var reader = BinaryLogReader.Create(Path.Join(buildCommand.WorkingDirectory, "msbuild.binlog"));
        var call = reader.ReadAllCompilerCalls().Should().ContainSingle().Subject;
        Path.GetFileName(call.CompilerFilePath).Should().Be("csc.dll");
    }
}
