// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.  See License.txt in the project root for license information.

using System.Collections.Immutable;
using Analyzer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.NetCore.Analyzers.Runtime
{
    using static MicrosoftNetCoreAnalyzersResources;

    /// <summary>
    /// CA2026: <inheritdoc cref="MissingShebangInFileBasedProgramTitle"/>
    /// </summary>
    public abstract class MissingShebangInFileBasedProgram : DiagnosticAnalyzer
    {
        internal const string RuleId = "CA2026";

        internal static readonly DiagnosticDescriptor Rule = DiagnosticDescriptorHelper.Create(
            RuleId,
            CreateLocalizableResourceString(nameof(MissingShebangInFileBasedProgramTitle)),
            CreateLocalizableResourceString(nameof(MissingShebangInFileBasedProgramMessage)),
            DiagnosticCategory.Reliability,
            RuleLevel.BuildWarning,
            CreateLocalizableResourceString(nameof(MissingShebangInFileBasedProgramDescription)),
            isPortedFxCopRule: false,
            isDataflowRule: false);

        public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);
    }
}
