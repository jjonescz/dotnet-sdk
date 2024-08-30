﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Net.Sdk.AnalyzerRedirecting.Tests;

public class SdkAnalyzerAssemblyRedirectorTests(ITestOutputHelper log) : SdkTest(log)
{
    [Fact]
    public void SameMajorMinorVersion()
    {
        TestDirectory testDir = _testAssetsManager.CreateTestDirectory(identifier: "RuntimeAnalyzers");

        var vsDir = Path.Combine(testDir.Path, "vs");
        var vsAnalyzerPath = FakeDll(vsDir, @"AspNetCoreAnalyzers\9.0.0-preview.5.24306.11\analyzers\dotnet\cs", "Microsoft.AspNetCore.App.Analyzers");
        var sdkAnalyzerPath = FakeDll(testDir.Path, @"sdk\packs\Microsoft.AspNetCore.App.Ref\9.0.0-preview.7.24406.2\analyzers\dotnet\cs", "Microsoft.AspNetCore.App.Analyzers");

        var resolver = new SdkAnalyzerAssemblyRedirector(vsDir);
        var redirected = resolver.RedirectPath(sdkAnalyzerPath);
        redirected.Should().Be(vsAnalyzerPath);
    }

    [Theory]
    [InlineData("8.0.100")]
    [InlineData("9.1.100")]
    public void DifferentMajorMinorVersion(string version)
    {
        TestDirectory testDir = _testAssetsManager.CreateTestDirectory(identifier: "RuntimeAnalyzers");

        var vsDir = Path.Combine(testDir.Path, "vs");
        FakeDll(vsDir, @$"AspNetCoreAnalyzers\{version}\analyzers\dotnet\cs", "Microsoft.AspNetCore.App.Analyzers");
        var sdkAnalyzerPath = FakeDll(testDir.Path, @"sdk\packs\Microsoft.AspNetCore.App.Ref\9.0.0-preview.7.24406.2\analyzers\dotnet\cs", "Microsoft.AspNetCore.App.Analyzers");

        var resolver = new SdkAnalyzerAssemblyRedirector(vsDir);
        var redirected = resolver.RedirectPath(sdkAnalyzerPath);
        redirected.Should().BeNull();
    }

    private static string FakeDll(string root, string subdir, string name)
    {
        var dllPath = Path.Combine(root, subdir, $"{name}.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dllPath));
        File.WriteAllText(dllPath, "");
        return dllPath;
    }
}
