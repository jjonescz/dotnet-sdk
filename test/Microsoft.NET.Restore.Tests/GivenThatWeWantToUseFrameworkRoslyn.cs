// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.Restore.Tests
{
    public class GivenThatWeWantToUseFrameworkRoslyn : SdkTest
    {
        public GivenThatWeWantToUseFrameworkRoslyn(ITestOutputHelper log) : base(log)
        {
        }

        private static void EnsureToolsetPackageCanBeRestored(TestAsset testAsset)
        {
            // Add built packages to NuGet.config so it is possible to download
            // the Microsoft.Net.Sdk.Compilers.Toolset package
            // (it is downloaded at the same version as the SDK
            // which does not exist in any feed at the time the test is running).
            var packages = Path.Combine(TestContext.GetRepoRoot() ?? AppContext.BaseDirectory, "artifacts", "packages", TestContext.RepoConfiguration, "NonShipping");
            NuGetConfigWriter.Write(testAsset.Path, packages);
        }

        [FullMSBuildOnlyFact]
        public void It_restores_Microsoft_Net_Compilers_Toolset_Framework_when_requested()
        {
            const string testProjectName = "NetCoreApp";
            var project = new TestProject
            {
                Name = testProjectName,
                TargetFrameworks = "net6.0",
            };

            project.AdditionalProperties.Add("BuildWithNetFrameworkHostedCompiler", "true");

            var testAsset = _testAssetsManager
                .CreateTestProject(project);

            EnsureToolsetPackageCanBeRestored(testAsset);

            var customPackageDir = Path.Combine(testAsset.Path, "nuget-packages");

            testAsset.GetRestoreCommand(Log, relativePath: testProjectName)
                .WithEnvironmentVariable("NUGET_PACKAGES", customPackageDir)
                .Execute().Should().Pass();

            Assert.True(Directory.Exists(Path.Combine(customPackageDir, "microsoft.net.sdk.compilers.toolset")));
        }

        [FullMSBuildOnlyFact]
        public void It_restores_Microsoft_Net_Compilers_Toolset_Framework_when_MSBuild_is_torn()
        {
            const string testProjectName = "NetCoreApp";
            var project = new TestProject
            {
                Name = testProjectName,
                TargetFrameworks = "net6.0",
            };

            // simulate mismatched MSBuild versions
            project.AdditionalProperties.Add("_IsDisjointMSBuildVersion", "true");

            var testAsset = _testAssetsManager
                .CreateTestProject(project);

            EnsureToolsetPackageCanBeRestored(testAsset);

            var customPackageDir = Path.Combine(testAsset.Path, "nuget-packages");

            testAsset.GetRestoreCommand(Log, relativePath: testProjectName)
                .WithEnvironmentVariable("NUGET_PACKAGES", customPackageDir)
                .Execute().Should().Pass();

            Assert.True(Directory.Exists(Path.Combine(customPackageDir, "microsoft.net.sdk.compilers.toolset")));
        }

        [FullMSBuildOnlyFact]
        public void It_does_not_throw_a_warning_when_adding_the_PackageReference_directly()
        {
            const string testProjectName = "NetCoreApp";
            var project = new TestProject
            {
                Name = testProjectName,
                TargetFrameworks = "net6.0",
            };

            project.PackageReferences.Add(new TestPackageReference("Microsoft.Net.Compilers.Toolset.Framework", "4.7.0-2.23260.7"));

            var testAsset = _testAssetsManager
                .CreateTestProject(project);

            var restoreCommand =
                testAsset.GetRestoreCommand(Log, relativePath: testProjectName);
            var result = restoreCommand.Execute();
            result.Should().Pass();
            result.Should().NotHaveStdOutContaining("NETSDK");
        }
    }
}
