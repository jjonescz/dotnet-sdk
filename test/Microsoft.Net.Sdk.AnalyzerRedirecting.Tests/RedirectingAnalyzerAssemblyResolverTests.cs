// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Microsoft.Net.Sdk.AnalyzerRedirecting.Tests;

public class RedirectingAnalyzerAssemblyResolverTests
{
    [Fact]
    public void Redirects()
    {
        Debug.Fail(TestContext.Current.TestAssetsDirectory);
    }
}
