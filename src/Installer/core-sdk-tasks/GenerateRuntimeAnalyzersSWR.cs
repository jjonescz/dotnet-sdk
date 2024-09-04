// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DotNet.Cli.Build
{
    public class GenerateRuntimeAnalyzersSWR : Task
    {
        [Required]
        public string RuntimeAnalyzersLayoutDirectory { get; set; }

        [Required]
        public string OutputFile { get; set; }

        public override bool Execute()
        {
            StringBuilder sb = new StringBuilder(SWR_HEADER);

            // NOTE: Keep in sync with SdkAnalyzerAssemblyRedirector.
            // This is intentionally short to avoid long path problems.
            const string installDir = @"DotNetRuntimeAnalyzers";

            AddFolder(sb,
                      @"AnalyzerRedirecting",
                      @"Common7\IDE\CommonExtensions\Microsoft\AnalyzerRedirecting",
                      filesToInclude:
                      [
                          "Microsoft.Net.Sdk.AnalyzerRedirecting.dll",
                          "Microsoft.Net.Sdk.AnalyzerRedirecting.pkgdef",
                          "extension.vsixmanifest",
                      ]);

            AddFolder(sb,
                      @"AspNetCoreAnalyzers",
                      @$"{installDir}\AspNetCoreAnalyzers");

            AddFolder(sb,
                      @"NetCoreAnalyzers",
                      @$"{installDir}\NetCoreAnalyzers");

            AddFolder(sb,
                      @"WindowsDesktopAnalyzers",
                      @$"{installDir}\WindowsDesktopAnalyzers");

            AddFolder(sb,
                      @"SDKAnalyzers",
                      @$"{installDir}\SDKAnalyzers");

            AddFolder(sb,
                      @"WebSDKAnalyzers",
                      @$"{installDir}\WebSDKAnalyzers");

            File.WriteAllText(OutputFile, sb.ToString());

            return true;
        }

        private void AddFolder(StringBuilder sb, string relativeSourcePath, string swrInstallDir, bool ngenAssemblies = true, ReadOnlySpan<string> filesToInclude = default)
        {
            string sourceFolder = Path.Combine(RuntimeAnalyzersLayoutDirectory, relativeSourcePath);
            var files = Directory.GetFiles(sourceFolder)
                            .Where(f => !Path.GetExtension(f).Equals(".pdb", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(f).Equals(".swr", StringComparison.OrdinalIgnoreCase))
                            .ToList();
            if (files.Any(f => !Path.GetFileName(f).Equals("_._")))
            {
                sb.Append(@"folder ""InstallDir:\");
                sb.Append(swrInstallDir);
                sb.AppendLine(@"\""");

                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);

                    if (!filesToInclude.IsEmpty && filesToInclude.IndexOf(fileName) < 0)
                    {
                        continue;
                    }

                    sb.Append(@"  file source=""$(PkgVS_Redist_Common_Net_Core_SDK_RuntimeAnalyzers)\");
                    sb.Append(Path.Combine(relativeSourcePath, fileName));
                    sb.Append('"');

                    if (ngenAssemblies && file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(@" vs.file.ngenApplications=""[installDir]\Common7\IDE\vsn.exe""");
                    }

                    sb.AppendLine();
                }

                sb.AppendLine();
            }

            if (!filesToInclude.IsEmpty)
            {
                return;
            }

            foreach (var subfolder in Directory.GetDirectories(sourceFolder))
            {
                string subfolderName = Path.GetFileName(subfolder);
                string newRelativeSourcePath = Path.Combine(relativeSourcePath, subfolderName);
                string newSwrInstallDir = Path.Combine(swrInstallDir, subfolderName);

                // Don't propagate ngenAssemblies to subdirectories.
                AddFolder(sb, newRelativeSourcePath, newSwrInstallDir);
            }
        }

        readonly string SWR_HEADER = @"use vs

package name=Microsoft.Net.Core.SDK.RuntimeAnalyzers
        version=$(ProductsBuildVersion)
        vs.package.internalRevision=$(PackageInternalRevision)

";
    }
}
