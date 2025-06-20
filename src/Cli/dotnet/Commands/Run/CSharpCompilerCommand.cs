// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CommandLine;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.NET.HostModel.AppHost;
using NuGet.Configuration;

namespace Microsoft.DotNet.Cli.Commands.Run;

/// <summary>
/// Used to invoke C# compiler in some optimized paths of <c>dotnet run file.cs</c>.
/// </summary>
internal sealed class CSharpCompilerCommand
{
    private static readonly SearchValues<char> s_additionalShouldSurroundWithQuotes = SearchValues.Create('=', ',');

    private static readonly ImmutableArray<string> s_pathOptions =
    [
        "reference:",
        "analyzer:",
        "additionalfile:",
        "analyzerconfig:",
        "embed:",
        "resource:",
        "linkresource:",
        "ruleset:",
        "keyfile:",
        "link:",
    ];

    private static string SdkPath => field ??= Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    private static string DotNetRootPath => field ??= Path.GetDirectoryName(Path.GetDirectoryName(SdkPath)!)!;
    private static string ClientDirectory => field ??= Path.Combine(SdkPath, "Roslyn", "bincore");
    private static string NuGetCachePath => field ??= SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(null));
    internal static string RuntimeVersion => field ??= RuntimeInformation.FrameworkDescription.Split(' ').Last();
    private const string TargetFrameworkVersion = VirtualProjectBuildingCommand.TargetFrameworkVersion;

    public required string EntryPointFileFullPath { get; init; }
    public required string ArtifactsPath { get; init; }
    public required bool CanReuseAuxiliaryFiles { get; init; }

    public int Execute()
    {
        // Write .rsp file and other intermediate build outputs.
        PrepareAuxiliaryFiles(out string rspPath);

        // Create a request for the compiler server
        // (this is much faster than starting a csc.dll process, especially on Windows).
        var buildRequest = BuildServerConnection.CreateBuildRequest(
            requestId: EntryPointFileFullPath,
            language: RequestLanguage.CSharpCompile,
            arguments: ["/noconfig", "/nologo", $"@{EscapeSingleArg(rspPath)}"],
            workingDirectory: Environment.CurrentDirectory,
            tempDirectory: Path.GetTempPath(),
            keepAlive: null,
            libDirectory: null,
            compilerHash: GetCompilerCommitHash());

        // Get pipe name.
        var pipeName = BuildServerConnection.GetPipeName(clientDirectory: ClientDirectory);

        // Create logger.
        var logger = new CompilerServerLogger(
            identifier: $"dotnet run file {Environment.ProcessId}",
            loggingFilePath: null);

        // Send the request.
        var responseTask = BuildServerConnection.RunServerBuildRequestAsync(
            buildRequest,
            pipeName: pipeName,
            clientDirectory: ClientDirectory,
            logger,
            cancellationToken: default);

        // Process the response.
        return ProcessBuildResponse(responseTask.Result);

        static string GetCompilerCommitHash()
        {
            return typeof(CSharpCompilation).Assembly.GetCustomAttributesData()
                .FirstOrDefault(attr => attr.AttributeType.FullName == "Microsoft.CodeAnalysis.CommitHashAttribute")?
                .ConstructorArguments
                .FirstOrDefault()
                .Value as string
                ?? throw new InvalidOperationException("Could not find compiler commit hash in the assembly attributes.");
        }

        static int ProcessBuildResponse(BuildResponse response)
        {
            switch (response)
            {
                case CompletedBuildResponse completed:
                    Reporter.Verbose.WriteLine("Compiler server processed compilation.");
                    Reporter.Output.Write(completed.Output);
                    return completed.ReturnCode;

                case IncorrectHashBuildResponse:
                    Reporter.Error.WriteLine("Compiler server reports a different hash version than the SDK.");
                    return 1;

                case null:
                    Reporter.Error.WriteLine("Could not launch the compiler server.");
                    return 1;

                default:
                    Reporter.Error.WriteLine($"Compiler server returned unexpected response: {response.GetType().Name}");
                    return 1;
            }
        }
    }

    private void PrepareAuxiliaryFiles(out string rspPath)
    {
        Reporter.Verbose.WriteLine(CanReuseAuxiliaryFiles
            ? "CSC auxiliary files can be reused."
            : "CSC auxiliary files can NOT be reused.");

        string fileDirectory = Path.GetDirectoryName(EntryPointFileFullPath) ?? string.Empty;
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(EntryPointFileFullPath);

        string objDir = Path.Join(ArtifactsPath, "obj", "debug");
        Directory.CreateDirectory(objDir);
        string binDir = Path.Join(ArtifactsPath, "bin", "debug");
        Directory.CreateDirectory(binDir);

        string assemblyAttributes = Path.Join(objDir, ".NETCoreApp,Version=v10.0.AssemblyAttributes.cs");
        if (ShouldEmit(assemblyAttributes))
        {
            File.WriteAllText(assemblyAttributes, /* lang=C#-test */ $"""
                // <autogenerated />
                using System;
                using System.Reflection;
                [assembly: global::System.Runtime.Versioning.TargetFrameworkAttribute(".NETCoreApp,Version=v{TargetFrameworkVersion}", FrameworkDisplayName = ".NET {TargetFrameworkVersion}")]

                """);
        }

        string globalUsings = Path.Join(objDir, $"{fileNameWithoutExtension}.GlobalUsings.g.cs");
        if (ShouldEmit(globalUsings))
        {
            File.WriteAllText(globalUsings, /* lang=C#-test */ """
                // <auto-generated/>
                global using System;
                global using System.Collections.Generic;
                global using System.IO;
                global using System.Linq;
                global using System.Net.Http;
                global using System.Threading;
                global using System.Threading.Tasks;

                """);
        }

        string assemblyInfo = Path.Join(objDir, $"{fileNameWithoutExtension}.AssemblyInfo.cs");
        if (ShouldEmit(assemblyInfo))
        {
            File.WriteAllText(assemblyInfo, /* lang=C#-test */ $"""
                // <autogenerated />
                using System;
                using System.Reflection;

                [assembly: System.Reflection.AssemblyCompanyAttribute("{fileNameWithoutExtension}")]
                [assembly: System.Reflection.AssemblyConfigurationAttribute("Debug")]
                [assembly: System.Reflection.AssemblyFileVersionAttribute("1.0.0.0")]
                [assembly: System.Reflection.AssemblyInformationalVersionAttribute("1.0.0")]
                [assembly: System.Reflection.AssemblyProductAttribute("{fileNameWithoutExtension}")]
                [assembly: System.Reflection.AssemblyTitleAttribute("{fileNameWithoutExtension}")]
                [assembly: System.Reflection.AssemblyVersionAttribute("1.0.0.0")]

                """);
        }

        string editorconfig = Path.Join(objDir, $"{fileNameWithoutExtension}.GeneratedMSBuildEditorConfig.editorconfig");
        if (ShouldEmit(editorconfig))
        {
            File.WriteAllText(editorconfig, $"""
                is_global = true
                build_property.TargetFramework = net{TargetFrameworkVersion}
                build_property.TargetFrameworkIdentifier = .NETCoreApp
                build_property.TargetFrameworkVersion = v{TargetFrameworkVersion}
                build_property.TargetPlatformMinVersion = 
                build_property.UsingMicrosoftNETSdkWeb = 
                build_property.ProjectTypeGuids = 
                build_property.InvariantGlobalization = 
                build_property.PlatformNeutralAssembly = 
                build_property.EnforceExtendedAnalyzerRules = 
                build_property._SupportedPlatformList = Linux,macOS,Windows
                build_property.RootNamespace = {fileNameWithoutExtension}
                build_property.ProjectDir = {fileDirectory}
                build_property.EnableComHosting = 
                build_property.EnableGeneratedComInterfaceComImportInterop = 
                build_property.EffectiveAnalysisLevelStyle = {TargetFrameworkVersion}
                build_property.EnableCodeStyleSeverity = 

                """);
        }

        var apphostTarget = Path.Join(binDir, $"{fileNameWithoutExtension}{FileNameSuffixes.CurrentPlatform.Exe}");
        if (ShouldEmit(apphostTarget))
        {
            var rid = RuntimeInformation.RuntimeIdentifier;
            var apphostSource = Path.Join(SdkPath, "..", "..", "packs", $"Microsoft.NETCore.App.Host.{rid}", RuntimeVersion, "runtimes", rid, "native", $"apphost{FileNameSuffixes.CurrentPlatform.Exe}");
            HostWriter.CreateAppHost(
                appHostSourceFilePath: apphostSource,
                appHostDestinationFilePath: apphostTarget,
                appBinaryFilePath: $"{fileNameWithoutExtension}.dll");
        }

        var runtimeConfig = Path.Join(binDir, $"{fileNameWithoutExtension}{FileNameSuffixes.RuntimeConfigJson}");
        if (ShouldEmit(runtimeConfig))
        {
            File.WriteAllText(runtimeConfig, $$"""
                {
                    "runtimeOptions": {
                        "tfm": "net10.0",
                        "framework": {
                            "name": "Microsoft.NETCore.App",
                            "version": {{JsonSerializer.Serialize(RuntimeVersion)}}
                        },
                        "configProperties": {
                            "EntryPointFilePath": {{JsonSerializer.Serialize(EntryPointFileFullPath)}},
                            "EntryPointFileDirectoryPath": {{JsonSerializer.Serialize(fileDirectory)}}
                        }
                    }
                }

                """);
        }

        rspPath = Path.Join(ArtifactsPath, "csc.rsp");
        if (ShouldEmit(rspPath))
        {
            // Use test `RunFileTests.CscArguments` to regenerate these.
            IEnumerable<string> args =
        [
                "/unsafe-",
                "/checked-",
                "/nowarn:1701,1702,IL2121,1701,1702",
                "/fullpaths",
                "/nostdlib+",
                "/errorreport:prompt",
                "/warn:10",
                "/define:TRACE;DEBUG;NET;NET10_0;NETCOREAPP;NET5_0_OR_GREATER;NET6_0_OR_GREATER;NET7_0_OR_GREATER;NET8_0_OR_GREATER;NET9_0_OR_GREATER;NET10_0_OR_GREATER;NETCOREAPP1_0_OR_GREATER;NETCOREAPP1_1_OR_GREATER;NETCOREAPP2_0_OR_GREATER;NETCOREAPP2_1_OR_GREATER;NETCOREAPP2_2_OR_GREATER;NETCOREAPP3_0_OR_GREATER;NETCOREAPP3_1_OR_GREATER",
                "/preferreduilang:en-US",
                "/highentropyva+",
                "/nullable:enable",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/Microsoft.CSharp.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/Microsoft.VisualBasic.Core.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/Microsoft.VisualBasic.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/Microsoft.Win32.Primitives.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/Microsoft.Win32.Registry.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/mscorlib.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/netstandard.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.AppContext.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Buffers.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Collections.Concurrent.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Collections.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Collections.Immutable.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Collections.NonGeneric.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Collections.Specialized.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.ComponentModel.Annotations.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.ComponentModel.DataAnnotations.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.ComponentModel.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.ComponentModel.EventBasedAsync.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.ComponentModel.Primitives.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.ComponentModel.TypeConverter.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Configuration.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Console.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Core.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Data.Common.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Data.DataSetExtensions.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Data.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Diagnostics.Contracts.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Diagnostics.Debug.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Diagnostics.DiagnosticSource.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Diagnostics.FileVersionInfo.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Diagnostics.Process.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Diagnostics.StackTrace.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Diagnostics.TextWriterTraceListener.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Diagnostics.Tools.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Diagnostics.TraceSource.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Diagnostics.Tracing.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Drawing.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Drawing.Primitives.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Dynamic.Runtime.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Formats.Asn1.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Formats.Tar.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Globalization.Calendars.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Globalization.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Globalization.Extensions.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.Compression.Brotli.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.Compression.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.Compression.FileSystem.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.Compression.ZipFile.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.FileSystem.AccessControl.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.FileSystem.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.FileSystem.DriveInfo.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.FileSystem.Primitives.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.FileSystem.Watcher.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.IsolatedStorage.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.MemoryMappedFiles.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.Pipelines.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.Pipes.AccessControl.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.Pipes.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.IO.UnmanagedMemoryStream.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Linq.AsyncEnumerable.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Linq.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Linq.Expressions.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Linq.Parallel.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Linq.Queryable.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Memory.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.Http.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.Http.Json.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.HttpListener.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.Mail.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.NameResolution.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.NetworkInformation.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.Ping.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.Primitives.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.Quic.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.Requests.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.Security.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.ServerSentEvents.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.ServicePoint.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.Sockets.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.WebClient.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.WebHeaderCollection.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.WebProxy.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.WebSockets.Client.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Net.WebSockets.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Numerics.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Numerics.Vectors.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.ObjectModel.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Reflection.DispatchProxy.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Reflection.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Reflection.Emit.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Reflection.Emit.ILGeneration.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Reflection.Emit.Lightweight.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Reflection.Extensions.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Reflection.Metadata.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Reflection.Primitives.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Reflection.TypeExtensions.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Resources.Reader.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Resources.ResourceManager.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Resources.Writer.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.CompilerServices.Unsafe.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.CompilerServices.VisualC.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.Extensions.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.Handles.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.InteropServices.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.InteropServices.JavaScript.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.InteropServices.RuntimeInformation.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.Intrinsics.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.Loader.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.Numerics.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.Serialization.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.Serialization.Formatters.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.Serialization.Json.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.Serialization.Primitives.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Runtime.Serialization.Xml.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.AccessControl.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.Claims.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.Cryptography.Algorithms.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.Cryptography.Cng.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.Cryptography.Csp.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.Cryptography.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.Cryptography.Encoding.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.Cryptography.OpenSsl.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.Cryptography.Primitives.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.Cryptography.X509Certificates.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.Principal.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.Principal.Windows.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Security.SecureString.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.ServiceModel.Web.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.ServiceProcess.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Text.Encoding.CodePages.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Text.Encoding.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Text.Encoding.Extensions.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Text.Encodings.Web.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Text.Json.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Text.RegularExpressions.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Threading.Channels.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Threading.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Threading.Overlapped.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Threading.Tasks.Dataflow.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Threading.Tasks.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Threading.Tasks.Extensions.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Threading.Tasks.Parallel.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Threading.Thread.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Threading.ThreadPool.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Threading.Timer.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Transactions.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Transactions.Local.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.ValueTuple.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Web.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Web.HttpUtility.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Windows.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Xml.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Xml.Linq.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Xml.ReaderWriter.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Xml.Serialization.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Xml.XDocument.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Xml.XmlDocument.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Xml.XmlSerializer.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Xml.XPath.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/System.Xml.XPath.XDocument.dll",
                $"/reference:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/ref/net10.0/WindowsBase.dll",
                "/debug+",
                "/debug:portable",
                "/filealign:512",
                "/optimize-",
                $"/out:{binDir}/{fileNameWithoutExtension}.dll",
                "/target:exe",
                "/warnaserror-",
                "/utf8output",
                "/deterministic+",
                "/langversion:13.0",
                "/features:FileBasedProgram",
                $"/analyzerconfig:{SdkPath}/Sdks/Microsoft.NET.Sdk/codestyle/cs/build/config/analysislevelstyle_default.globalconfig",
                $"/analyzerconfig:{objDir}/{fileNameWithoutExtension}.GeneratedMSBuildEditorConfig.editorconfig",
                $"/analyzerconfig:{SdkPath}/Sdks/Microsoft.NET.Sdk/analyzers/build/config/analysislevel_10_default.globalconfig",
                $"/analyzer:{SdkPath}/Sdks/Microsoft.NET.Sdk/targets/../analyzers/Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll",
                $"/analyzer:{SdkPath}/Sdks/Microsoft.NET.Sdk/targets/../analyzers/Microsoft.CodeAnalysis.NetAnalyzers.dll",
                $"/analyzer:{NuGetCachePath}/microsoft.net.illink.tasks/{RuntimeVersion}/analyzers/dotnet/cs/ILLink.CodeFixProvider.dll",
                $"/analyzer:{NuGetCachePath}/microsoft.net.illink.tasks/{RuntimeVersion}/analyzers/dotnet/cs/ILLink.RoslynAnalyzer.dll",
                $"/analyzer:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/analyzers/dotnet/cs/Microsoft.Interop.ComInterfaceGenerator.dll",
                $"/analyzer:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/analyzers/dotnet/cs/Microsoft.Interop.JavaScript.JSImportGenerator.dll",
                $"/analyzer:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/analyzers/dotnet/cs/Microsoft.Interop.LibraryImportGenerator.dll",
                $"/analyzer:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/analyzers/dotnet/cs/Microsoft.Interop.SourceGeneration.dll",
                $"/analyzer:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/analyzers/dotnet/cs/System.Text.Json.SourceGeneration.dll",
                $"/analyzer:{DotNetRootPath}/packs/Microsoft.NETCore.App.Ref/{RuntimeVersion}/analyzers/dotnet/cs/System.Text.RegularExpressions.Generator.dll",
                $"{EntryPointFileFullPath}",
                $"{objDir}/{fileNameWithoutExtension}.GlobalUsings.g.cs",
                $"{objDir}/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs",
                $"{objDir}/{fileNameWithoutExtension}.AssemblyInfo.cs",
                "/warnaserror+:NU1605,SYSLIB0011",
            ];

            File.WriteAllLines(rspPath, args.Select(EscapeSingleArg));
        }

        bool ShouldEmit(string file)
        {
            if (!CanReuseAuxiliaryFiles)
            {
                return true;
            }

            if (!File.Exists(file))
            {
                Reporter.Verbose.WriteLine($"Generating CSC auxiliary file because it does not exist: {file}");
                return true;
            }

            return false;
        }
    }

    private static string EscapeSingleArg(string arg)
    {
        if (IsPathOption(arg, out var colonIndex))
        {
            return arg[..(colonIndex + 1)] + EscapeCore(arg[(colonIndex + 1)..]);
        }

        return EscapeCore(arg);

        static string EscapeCore(string arg)
        {
            return ArgumentEscaper.EscapeSingleArg(arg, additionalShouldSurroundWithQuotes: static (string arg) =>
            {
                return arg.ContainsAny(s_additionalShouldSurroundWithQuotes);
            });
        }

        static bool IsPathOption(string arg, out int colonIndex)
        {
            if (!arg.StartsWith('/'))
            {
                colonIndex = -1;
                return false;
            }

            var span = arg.AsSpan(start: 1);
            foreach (var optionName in s_pathOptions)
            {
                Debug.Assert(!optionName.StartsWith('/') && optionName.EndsWith(':'));

                if (span.StartsWith(optionName, StringComparison.OrdinalIgnoreCase))
                {
                    colonIndex = optionName.Length;
                    return true;
                }
            }

            colonIndex = -1;
            return false;
        }
    }
}
