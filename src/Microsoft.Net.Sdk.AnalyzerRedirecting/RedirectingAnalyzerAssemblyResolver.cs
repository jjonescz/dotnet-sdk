// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using Microsoft.CodeAnalysis;
using System.Reflection;

namespace Microsoft.Net.Sdk.AnalyzerRedirecting;

public sealed class RedirectingAnalyzerAssemblyResolver : IAnalyzerAssemblyResolver
{
    public Assembly? ResolveAssembly(AssemblyName assemblyName, string assemblyOriginalDirectory)
    {
        return null;
    }
}
