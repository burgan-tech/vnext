using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CustomScriptHelpersDemo.Engine;

/// <summary>
/// Semantic guard that runs after a <see cref="Compilation"/> is built but before
/// IL is emitted. It resolves every referenced symbol and rejects any whose
/// containing namespace falls under a banned prefix, plus <c>DllImport</c> and
/// <c>unsafe</c>. This catches dangerous types that live in mandatory assemblies
/// (e.g. <c>System.IO.File</c>) which reference-omission alone cannot block.
/// </summary>
public static class BannedApiAnalyzer
{
    public static IReadOnlyList<string> Analyze(Compilation compilation, SandboxOptions options)
    {
        var violations = new List<string>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            // 1. Banned namespace usage — resolve symbols on every name/expression node.
            foreach (var node in root.DescendantNodes())
            {
                if (node is not (ExpressionSyntax or AttributeSyntax))
                    continue;

                var symbol = model.GetSymbolInfo(node).Symbol;
                var ns = NamespaceOf(symbol);
                if (ns is null)
                    continue;

                var banned = options.BannedNamespaces
                    .FirstOrDefault(b => ns == b || ns.StartsWith(b + ".", StringComparison.Ordinal));

                if (banned is not null)
                {
                    var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    violations.Add($"{Path.GetFileName(tree.FilePath)}({line}): banned namespace '{banned}' via '{symbol}'");
                }
            }

            // 2. P/Invoke.
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (method.AttributeLists.SelectMany(a => a.Attributes)
                    .Any(a => a.Name.ToString().Contains("DllImport")))
                {
                    var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    violations.Add($"{Path.GetFileName(tree.FilePath)}({line}): P/Invoke (DllImport) is not allowed");
                }
            }

            // 3. unsafe code.
            if (!options.AllowUnsafe &&
                root.DescendantTokens().Any(t => t.IsKind(SyntaxKind.UnsafeKeyword)))
            {
                violations.Add($"{Path.GetFileName(tree.FilePath)}: 'unsafe' code is not allowed");
            }
        }

        return violations.Distinct().ToList();
    }

    private static string? NamespaceOf(ISymbol? symbol)
    {
        var type = symbol switch
        {
            null => null,
            ITypeSymbol t => t,
            _ => symbol.ContainingType,
        };
        return type?.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString()
            : null;
    }
}
