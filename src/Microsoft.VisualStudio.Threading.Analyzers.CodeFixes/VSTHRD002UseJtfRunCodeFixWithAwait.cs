﻿/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeActions;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Simplification;
    using Microsoft.VisualStudio.Threading;

    [ExportCodeFixProvider(LanguageNames.CSharp)]
    public class VSTHRD002UseJtfRunCodeFixWithAwait : CodeFixProvider
    {
        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            VSTHRD002UseJtfRunAnalyzer.Id);

        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();

            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            if (TryFindNodeAtSource(diagnostic, root, out _, out _))
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        Strings.VSTHRD002_CodeFix_Await_Title,
                        async ct =>
                        {
                            var document = context.Document;
                            if (TryFindNodeAtSource(diagnostic, root, out var node, out var transform))
                            {
                                (document, node, _) = await FixUtils.UpdateDocumentAsync(
                                    document,
                                    node,
                                    n => SyntaxFactory.AwaitExpression(transform(n, ct)),
                                    ct).ConfigureAwait(false);
                                var method = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                                if (method != null)
                                {
                                    (document, method) = await FixUtils.MakeMethodAsync(method, document, ct).ConfigureAwait(false);
                                }
                            }

                            return document.Project.Solution;
                        },
                        "only action"),
                    diagnostic);
            }
        }

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        private static bool TryFindNodeAtSource(Diagnostic diagnostic, SyntaxNode root, [NotNullWhen(true)] out ExpressionSyntax? target, [NotNullWhen(true)] out Func<ExpressionSyntax, CancellationToken, ExpressionSyntax>? transform)
        {
            transform = null;
            target = null;

            var syntaxNode = (ExpressionSyntax)root.FindNode(diagnostic.Location.SourceSpan);
            if (syntaxNode.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() != null)
            {
                // We don't support converting anonymous delegates to async.
                return false;
            }

            SimpleNameSyntax? FindStaticWaitInvocation(ExpressionSyntax from, CancellationToken cancellationToken = default(CancellationToken))
            {
                var name = ((from as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax)?.Name;
                return name?.Identifier.ValueText switch
                {
                    nameof(Task.WaitAny) => name,
                    nameof(Task.WaitAll) => name,
                    _ => null,
                };
            }

            ExpressionSyntax? TransformStaticWhatInvocation(ExpressionSyntax from, CancellationToken cancellationToken = default(CancellationToken))
            {
                var name = FindStaticWaitInvocation(from);
                var newIdentifier = name!.Identifier.ValueText switch
                {
                    nameof(Task.WaitAny) => nameof(Task.WhenAny),
                    nameof(Task.WaitAll) => nameof(Task.WhenAll),
                    _ => throw new InvalidOperationException(),
                };

                return from.ReplaceToken(name.Identifier, SyntaxFactory.Identifier(newIdentifier)).WithoutAnnotations(FixUtils.BookmarkAnnotationName);
            }

            ExpressionSyntax? FindTwoLevelDeepIdentifierInvocation(ExpressionSyntax from, CancellationToken cancellationToken = default(CancellationToken)) =>
                ((((from as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax)?.Expression as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax)?.Expression;
            ExpressionSyntax? FindOneLevelDeepIdentifierInvocation(ExpressionSyntax from, CancellationToken cancellationToken = default(CancellationToken)) =>
                ((from as InvocationExpressionSyntax)?.Expression as MemberAccessExpressionSyntax)?.Expression;
            ExpressionSyntax? FindParentMemberAccess(ExpressionSyntax from, CancellationToken cancellationToken = default(CancellationToken)) =>
                (from as MemberAccessExpressionSyntax)?.Expression;

            var parentInvocation = syntaxNode.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            var parentMemberAccess = syntaxNode.FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
            if (FindTwoLevelDeepIdentifierInvocation(parentInvocation) != null)
            {
                // This method will not return null for the provided 'target' argument
                transform = NullableHelpers.AsNonNullReturnUnchecked<ExpressionSyntax, CancellationToken, ExpressionSyntax>(FindTwoLevelDeepIdentifierInvocation);
                target = parentInvocation;
            }
            else if (FindStaticWaitInvocation(parentInvocation) != null)
            {
                // This method will not return null for the provided 'target' argument
                transform = NullableHelpers.AsNonNullReturnUnchecked<ExpressionSyntax, CancellationToken, ExpressionSyntax>(TransformStaticWhatInvocation);
                target = parentInvocation;
            }
            else if (FindOneLevelDeepIdentifierInvocation(parentInvocation) != null)
            {
                // This method will not return null for the provided 'target' argument
                transform = NullableHelpers.AsNonNullReturnUnchecked<ExpressionSyntax, CancellationToken, ExpressionSyntax>(FindOneLevelDeepIdentifierInvocation);
                target = parentInvocation;
            }
            else if (FindParentMemberAccess(parentMemberAccess) != null)
            {
                // This method will not return null for the provided 'target' argument
                transform = NullableHelpers.AsNonNullReturnUnchecked<ExpressionSyntax, CancellationToken, ExpressionSyntax>(FindParentMemberAccess);
                target = parentMemberAccess;
            }
            else
            {
                return false;
            }

            return true;
        }
    }
}
