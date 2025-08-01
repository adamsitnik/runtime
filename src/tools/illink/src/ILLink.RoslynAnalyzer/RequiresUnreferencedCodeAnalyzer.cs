// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Immutable;
using System.Linq;
using ILLink.Shared;
using ILLink.Shared.TrimAnalysis;
using ILLink.Shared.TypeSystemProxy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ILLink.RoslynAnalyzer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RequiresUnreferencedCodeAnalyzer : RequiresAnalyzerBase
    {
        public const string RequiresUnreferencedCodeAttribute = nameof(RequiresUnreferencedCodeAttribute);
        public const string FullyQualifiedRequiresUnreferencedCodeAttribute = "System.Diagnostics.CodeAnalysis." + RequiresUnreferencedCodeAttribute;

        private static readonly DiagnosticDescriptor s_requiresUnreferencedCodeRule = DiagnosticDescriptors.GetDiagnosticDescriptor(DiagnosticId.RequiresUnreferencedCode);
        private static readonly DiagnosticDescriptor s_requiresUnreferencedCodeAttributeMismatch = DiagnosticDescriptors.GetDiagnosticDescriptor(DiagnosticId.RequiresUnreferencedCodeAttributeMismatch);
        private static readonly DiagnosticDescriptor s_makeGenericTypeRule = DiagnosticDescriptors.GetDiagnosticDescriptor(DiagnosticId.MakeGenericType);
        private static readonly DiagnosticDescriptor s_makeGenericMethodRule = DiagnosticDescriptors.GetDiagnosticDescriptor(DiagnosticId.MakeGenericMethod);
        private static readonly DiagnosticDescriptor s_requiresUnreferencedCodeOnStaticCtor = DiagnosticDescriptors.GetDiagnosticDescriptor(DiagnosticId.RequiresUnreferencedCodeOnStaticConstructor);
        private static readonly DiagnosticDescriptor s_requiresUnreferencedCodeOnEntryPoint = DiagnosticDescriptors.GetDiagnosticDescriptor(DiagnosticId.RequiresUnreferencedCodeOnEntryPoint);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(s_makeGenericMethodRule, s_makeGenericTypeRule, s_requiresUnreferencedCodeRule, s_requiresUnreferencedCodeAttributeMismatch, s_requiresUnreferencedCodeOnStaticCtor, s_requiresUnreferencedCodeOnEntryPoint);

        private protected override string RequiresAttributeName => RequiresUnreferencedCodeAttribute;

        internal override string RequiresAttributeFullyQualifiedName => FullyQualifiedRequiresUnreferencedCodeAttribute;

        private protected override DiagnosticTargets AnalyzerDiagnosticTargets => DiagnosticTargets.MethodOrConstructor | DiagnosticTargets.Class;

        private protected override DiagnosticDescriptor RequiresDiagnosticRule => s_requiresUnreferencedCodeRule;

        private protected override DiagnosticId RequiresDiagnosticId => DiagnosticId.RequiresUnreferencedCode;

        private protected override DiagnosticDescriptor RequiresAttributeMismatch => s_requiresUnreferencedCodeAttributeMismatch;

        private protected override DiagnosticDescriptor RequiresOnStaticCtor => s_requiresUnreferencedCodeOnStaticCtor;

        private protected override DiagnosticDescriptor RequiresOnEntryPoint => s_requiresUnreferencedCodeOnEntryPoint;

        internal override bool IsAnalyzerEnabled(AnalyzerOptions options) =>
            options.IsMSBuildPropertyValueTrue(MSBuildPropertyOptionNames.EnableTrimAnalyzer);

        private protected override bool IsRequiresCheck(IPropertySymbol propertySymbol, Compilation compilation)
        {
            // "IsUnreferencedCodeSupported" is treated as a requires check for testing purposes only, and
            // is not officially-supported product behavior.
            var runtimeFeaturesType = compilation.GetTypeByMetadataName("ILLink.RoslynAnalyzer.TestFeatures");
            if (runtimeFeaturesType == null)
                return false;

            var isDynamicCodeSupportedProperty = runtimeFeaturesType.GetMembers("IsUnreferencedCodeSupported").OfType<IPropertySymbol>().FirstOrDefault();
            if (isDynamicCodeSupportedProperty == null)
                return false;

            return SymbolEqualityComparer.Default.Equals(propertySymbol, isDynamicCodeSupportedProperty);
        }

        protected override bool CreateSpecialIncompatibleMembersDiagnostic(
            ImmutableArray<ISymbol> specialIncompatibleMembers,
            ISymbol member,
            in DiagnosticContext diagnosticContext)
        {
            // Some RUC-annotated APIs are intrinsically handled by the trimmer
            if (member is IMethodSymbol method && Intrinsics.GetIntrinsicIdForMethod(new MethodProxy(method)) != IntrinsicId.None)
            {
                return true;
            }

            return false;
        }

        protected override bool VerifyAttributeArguments(AttributeData attribute) =>
            RequiresUnreferencedCodeUtils.VerifyRequiresUnreferencedCodeAttributeArguments(attribute);

        protected override string GetMessageFromAttribute(AttributeData? requiresAttribute) =>
            RequiresUnreferencedCodeUtils.GetMessageFromAttribute(requiresAttribute);
    }
}
