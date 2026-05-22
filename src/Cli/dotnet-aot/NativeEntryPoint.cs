// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.NativeWrapper;

namespace Microsoft.DotNet.Cli;

internal static class AotHostContext
{
    public static string SdkDir { get; private set; } = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    public static string DotNetRoot { get; private set; } = Path.GetDirectoryName(Path.GetDirectoryName(SdkDir)!)!;

    public static void Initialize(string dotnetRoot, string sdkDir)
    {
        string resolvedDotNetRoot = !string.IsNullOrEmpty(dotnetRoot)
            ? Path.TrimEndingDirectorySeparator(dotnetRoot)
            : Path.GetDirectoryName(Path.GetDirectoryName(SdkDir)!)!;

        if (!string.IsNullOrEmpty(sdkDir))
        {
            SdkDir = ResolveSdkDir(Path.TrimEndingDirectorySeparator(sdkDir), resolvedDotNetRoot);
        }

        DotNetRoot = resolvedDotNetRoot;
    }

    private static string ResolveSdkDir(string sdkDir, string dotNetRoot)
    {
        if (File.Exists(Path.Join(sdkDir, "Roslyn", "bincore", "csc.dll")))
        {
            return sdkDir;
        }

        string sdkRoot = Path.Join(dotNetRoot, "sdk");
        if (!Directory.Exists(sdkRoot))
        {
            return sdkDir;
        }

        return Directory.GetDirectories(sdkRoot)
            .Where(static candidate => File.Exists(Path.Join(candidate, "Roslyn", "bincore", "csc.dll")))
            .OrderDescending(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? sdkDir;
    }
}

static unsafe partial class NativeEntryPoint
{
    [UnmanagedCallersOnly(EntryPoint = "dotnet_execute")]
    static int Execute(
        nint hostPathPtr,      // const char_t* host_path
        nint dotnetRootPtr,    // const char_t* dotnet_root
        nint sdkDirPtr,        // const char_t* sdk_dir
        nint hostfxrPathPtr,   // const char_t* hostfxr_path
        int argc,              // int argc (user args, no dotnet exe)
        nint argvPtr)          // const char_t** argv
    {
        string hostPath = PlatformStringMarshaller.ConvertToManaged(hostPathPtr) ?? string.Empty;
        string dotnetRoot = PlatformStringMarshaller.ConvertToManaged(dotnetRootPtr) ?? string.Empty;
        string sdkDir = PlatformStringMarshaller.ConvertToManaged(sdkDirPtr) ?? string.Empty;
        string hostfxrPath = PlatformStringMarshaller.ConvertToManaged(hostfxrPathPtr) ?? string.Empty;

        AotHostContext.Initialize(dotnetRoot, sdkDir);

        // Make hostfxr discoverable for NativeWrapper P/Invokes (required on non-Windows)
        if (!string.IsNullOrEmpty(hostfxrPath))
        {
            AppContext.SetData("HOSTFXR_PATH", hostfxrPath);
        }

        string[] args = new string[argc];
        nint* argv = (nint*)argvPtr;
        for (int i = 0; i < argc; i++)
        {
            args[i] = PlatformStringMarshaller.ConvertToManaged(argv[i]) ?? string.Empty;
        }

        // Try the AOT-compiled path for supported commands (if enabled)
        if (EnvironmentVariableParser.ParseBool(Environment.GetEnvironmentVariable(EnvironmentVariableNames.DOTNET_CLI_ENABLEAOT), defaultValue: false))
        {
            var parseResult = Parser.Parse(args);
            if (parseResult.Errors.Count == 0)
            {
                int exitCode = Parser.Invoke(parseResult);
                if (exitCode != Parser.FallbackToManagedCli)
                {
                    return exitCode;
                }
            }
        }

        // Fall back to the fully managed dotnet CLI by hosting .NET
        string dotnetDll = Path.Join(sdkDir, "dotnet.dll");
        string runtimeConfig = Path.Join(sdkDir, "dotnet.runtimeconfig.json");

        if (File.Exists(dotnetDll) && File.Exists(runtimeConfig))
        {
            // Use the command-line hosting path to run dotnet.dll
            string[] appArgs = new string[args.Length + 1];
            appArgs[0] = dotnetDll;
            Array.Copy(args, 0, appArgs, 1, args.Length);
            return ManagedHost.RunApp(hostPath, dotnetRoot, hostfxrPath, appArgs);
        }

        // No managed fallback available
        Console.Error.WriteLine($"The managed fallback could not be located. Expected '{dotnetDll}' and '{runtimeConfig}'.");
        return 1;
    }
}
