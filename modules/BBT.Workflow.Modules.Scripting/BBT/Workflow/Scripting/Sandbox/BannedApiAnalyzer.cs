using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BBT.Workflow.Scripting.Sandbox;

/// <summary>
/// Semantic guard that runs after a <see cref="Compilation"/> is built but before IL is emitted.
/// It resolves every referenced symbol and rejects any whose containing namespace falls under a
/// banned prefix, plus <c>DllImport</c> and <c>unsafe</c>. This catches dangerous types that live
/// in mandatory assemblies (e.g. <c>System.IO.File</c>) which reference omission alone cannot block.
///
/// The banned set is the <see cref="MandatoryBannedNamespaces"/> baseline (platform-owned,
/// non-overridable) unioned with any operator additions; <see cref="MandatoryAllowedNamespaces"/>
/// carves out sub-namespaces that must remain usable (notably <c>System.Threading.Tasks</c>, so
/// banning thread/synchronization primitives does not break <c>Task</c>-based async).
/// </summary>
public static class BannedApiAnalyzer
{
    /// <summary>
    /// Mandatory, non-overridable banned namespace prefixes. A mapping compile must never perform
    /// file IO, network/HTTP, process/diagnostics, reflection, native interop, or registry access.
    /// Config may add to this set but never remove from it.
    /// Note: <c>System.Threading</c> is intentionally NOT banned — threading/synchronization primitives
    /// (and <c>System.Threading.Tasks</c>) are allowed for mappings.
    /// </summary>
    public static readonly IReadOnlyList<string> MandatoryBannedNamespaces = new[]
    {
        "System.IO",
        "System.Net",
        "System.Net.Http",
        "System.Diagnostics",
        "System.Reflection",
        "System.Runtime.InteropServices",
        "Microsoft.Win32",
    };

    /// <summary>
    /// Carve-outs that remain allowed even when nested under a banned prefix. Currently empty; retained
    /// as an extension point should a future banned prefix require a usable sub-namespace.
    /// </summary>
    public static readonly IReadOnlyList<string> MandatoryAllowedNamespaces = System.Array.Empty<string>();

    /// <summary>
    /// Analyzes the compilation and returns the list of human-readable sandbox violations
    /// (empty when the script is clean).
    /// </summary>
    /// <param name="compilation">The compilation to analyze.</param>
    /// <param name="options">Sandbox options (used for <c>AllowUnsafe</c> and configured ban additions).</param>
    /// <param name="includeConfiguredBans">
    /// When <c>true</c>, operator-configured <see cref="ScriptSandboxOptions.BannedNamespaces"/> are
    /// unioned with the mandatory baseline. When <c>false</c>, only the mandatory (non-overridable)
    /// baseline is enforced — used when the sandbox is disabled so mapping code can still never touch
    /// IO/network/reflection/etc. <c>DllImport</c> and <c>unsafe</c> are always enforced.
    /// </param>
    public static IReadOnlyList<string> Analyze(
        Compilation compilation,
        ScriptSandboxOptions options,
        bool includeConfiguredBans = true)
    {
        var banned = MandatoryBannedNamespaces
            .Concat(includeConfiguredBans
                ? options.BannedNamespaces ?? Enumerable.Empty<string>()
                : Enumerable.Empty<string>())
            .Distinct()
            .ToList();

        var allowedCarveOuts = MandatoryAllowedNamespaces;

        var violations = new List<string>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            // 1. Banned namespace usage — resolve symbols on every name/expression/attribute node.
            foreach (var node in root.DescendantNodes())
            {
                if (node is not (ExpressionSyntax or AttributeSyntax))
                    continue;

                var symbol = model.GetSymbolInfo(node).Symbol;
                var ns = NamespaceOf(symbol);
                if (ns is null)
                    continue;

                // Carve-outs win over the banned prefixes.
                if (allowedCarveOuts.Any(a => ns == a || ns.StartsWith(a + ".", System.StringComparison.Ordinal)))
                    continue;

                var hit = banned.FirstOrDefault(b =>
                    ns == b || ns.StartsWith(b + ".", System.StringComparison.Ordinal));

                if (hit is not null)
                {
                    var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    violations.Add(
                        $"{Path.GetFileName(tree.FilePath)}({line}): banned namespace '{hit}' via '{symbol}'");
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
