// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Microsoft.NET.Build.Tests
{
    public class GivenThatWeWantToUseBuildLoadIsolation : SdkTest
    {
        public GivenThatWeWantToUseBuildLoadIsolation(ITestOutputHelper log) : base(log)
        {
        }

        [WindowsOnlyFact]
        public void It_remaps_analyzers_when_enabled()
        {
            var testAsset = CreateAnalyzerTestAsset();

            var analyzers = GetAnalyzers(testAsset, "/p:EnableWindowsBuildLoadIsolation=true");

            analyzers.Should().NotBeEmpty();
            analyzers.Should().OnlyContain(path => IsInBuildLoadIsolation(path));
        }

        [WindowsOnlyFact]
        public void It_leaves_analyzers_at_original_paths_when_disabled()
        {
            var testAsset = CreateAnalyzerTestAsset();

            var analyzers = GetAnalyzers(testAsset);

            analyzers.Should().NotBeEmpty();
            analyzers.Should().NotContain(path => IsInBuildLoadIsolation(path));
        }

        private TestAsset CreateAnalyzerTestAsset([CallerMemberName] string identifier = "")
        {
            var testProject = new TestProject("BuildLoadIsolationAnalyzers")
            {
                TargetFrameworks = ToolsetInfo.CurrentTargetFramework,
            };
            testProject.AdditionalProperties["EnableNETAnalyzers"] = "true";
            testProject.SourceFiles["Class1.cs"] = "public class Class1 { }";

            return _testAssetsManager.CreateTestProject(testProject, identifier: identifier);
        }

        private static List<string> GetAnalyzers(TestAsset testAsset, params string[] args)
        {
            var getValuesCommand = new GetValuesCommand(testAsset, "Analyzer", GetValuesCommand.ValueType.Item, ToolsetInfo.CurrentTargetFramework);

            getValuesCommand.Execute(args)
                .Should()
                .Pass();

            return getValuesCommand.GetValues();
        }

        private static bool IsInBuildLoadIsolation(string path)
        {
            return path.Contains($"{Path.DirectorySeparatorChar}build-load-isolation{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        }
    }
}