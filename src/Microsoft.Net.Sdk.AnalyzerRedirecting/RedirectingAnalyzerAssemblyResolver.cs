// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
using System.ComponentModel.Composition;
using System.Reflection;

namespace Microsoft.Net.Sdk.AnalyzerRedirecting;

[Export(typeof(IAnalyzerAssemblyResolver))]
public sealed class RedirectingAnalyzerAssemblyResolver : IAnalyzerAssemblyResolver
{
    public Assembly? ResolveAssembly(AssemblyName assemblyName, string assemblyOriginalDirectory)
    {
        return null;
    }
}
