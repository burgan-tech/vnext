using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BBT.Workflow.Scripting.Sandbox;

/// <summary>
/// Semantic guard that runs after a <see cref="Compilation"/> is built but before IL is emitted.
/// It resolves referenced symbols and rejects any whose containing namespace falls under a
/// banned prefix, plus <c>DllImport</c> and <c>unsafe</c>. This catches dangerous types that live
/// in mandatory assemblies (e.g. <c>System.IO.File</c>) which reference omission alone cannot block.
///
/// The banned set is the <see cref="MandatoryBannedNamespaces"/> baseline (platform-owned,
/// non-overridable) unioned with any operator additions; <see cref="MandatoryAllowedNamespaces"/>
/// carves out sub-namespaces that must remain usable (notably <c>System.Threading.Tasks</c>, so
/// banning thread/synchronization primitives does not break <c>Task</c>-based async).
///
/// <para>
/// Cost discipline: the analyzer runs on EVERY compile, and <c>GetSymbolInfo</c> is a semantic
/// bind — the same work <c>Emit</c> does again later — so the walk is restricted to the node kinds
/// through which a symbol can actually be NAMED (simple names, member accesses, object creations,
/// attributes) instead of every expression, and both the per-namespace verdict and the namespace
/// display string are memoized per distinct symbol rather than recomputed per node. The verdicts
/// are unchanged — the same symbols resolve to the same namespaces — only the redundant binds and
/// string formatting are gone.
/// </para>
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
    /// Member-level carve-outs: specific members that live under a banned namespace but are benign
    /// metadata reads — they expose no code-execution or sandbox-escape capability — and so remain
    /// usable. Matched against the resolved symbol's display string. The dangerous reflection surface
    /// (e.g. <c>MethodInfo.Invoke</c>, <c>Assembly.Load</c>, <c>Activator</c>) stays banned.
    /// <para>
    /// <c>System.Reflection.MemberInfo.Name</c> backs the ubiquitous <c>value.GetType().Name</c>
    /// pattern (<c>Type.Name</c> resolves to the inherited <c>MemberInfo.Name</c>), used by mappings
    /// and the <c>ScriptBase</c> helper surface for diagnostics/branching on a value's type name.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> MandatoryAllowedMembers = new[]
    {
        "System.Reflection.MemberInfo.Name",
    };

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

        // Per-namespace verdict memo: the banned-prefix hit (or null) for a namespace symbol never
        // changes within one compilation, and scripts reference the same few namespaces repeatedly.
        var namespaceVerdicts = new Dictionary<INamespaceSymbol, string?>(SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            // One walk over the tree covers all three checks.
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    // 1. Banned namespace usage — a banned symbol is always reached through a name
                    // (simple or generic), a member access, an object creation, or an attribute;
                    // every other expression kind only combines values those nodes already produced.
                    case SimpleNameSyntax or MemberAccessExpressionSyntax or BaseObjectCreationExpressionSyntax
                        or AttributeSyntax:
                    {
                        var symbol = model.GetSymbolInfo(node).Symbol;
                        var hit = BannedHitOf(symbol, banned, allowedCarveOuts, namespaceVerdicts);
                        if (hit is null)
                            break;

                        // Member-level carve-outs (benign metadata reads) win over the banned
                        // prefixes. Only evaluated after a banned hit — the ToString is not free.
                        if (symbol is not null && MandatoryAllowedMembers.Contains(symbol.ToString()))
                            break;

                        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        violations.Add(
                            $"{Path.GetFileName(tree.FilePath)}({line}): banned namespace '{hit}' via '{symbol}'");
                        break;
                    }

                    // 2. P/Invoke.
                    case MethodDeclarationSyntax method:
                    {
                        if (method.AttributeLists.SelectMany(a => a.Attributes)
                            .Any(a => a.Name.ToString().Contains("DllImport")))
                        {
                            var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            violations.Add(
                                $"{Path.GetFileName(tree.FilePath)}({line}): P/Invoke (DllImport) is not allowed");
                        }

                        break;
                    }
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

    /// <summary>
    /// The banned prefix the symbol's containing namespace falls under, or null. Verdicts are
    /// memoized per namespace symbol; the display string is only built on the memo's first sight
    /// of that namespace.
    /// </summary>
    private static string? BannedHitOf(
        ISymbol? symbol,
        List<string> banned,
        IReadOnlyList<string> allowedCarveOuts,
        Dictionary<INamespaceSymbol, string?> namespaceVerdicts)
    {
        var type = symbol switch
        {
            null => null,
            ITypeSymbol t => t,
            _ => symbol.ContainingType,
        };

        if (type?.ContainingNamespace is not { IsGlobalNamespace: false } ns)
            return null;

        if (namespaceVerdicts.TryGetValue(ns, out var verdict))
            return verdict;

        var name = ns.ToDisplayString();

        verdict = allowedCarveOuts.Any(a => name == a || name.StartsWith(a + ".", System.StringComparison.Ordinal))
            ? null
            : banned.FirstOrDefault(b => name == b || name.StartsWith(b + ".", System.StringComparison.Ordinal));

        namespaceVerdicts[ns] = verdict;
        return verdict;
    }
}
