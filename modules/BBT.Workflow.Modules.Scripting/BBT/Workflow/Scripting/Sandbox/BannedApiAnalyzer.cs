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
    /// Type-level carve-outs under a banned namespace: in-memory stream primitives scripts need to process a file's
    /// bytes (vnext-client-sdk-core#142-A) — none of them opens a path, a handle or a socket on its own. The members of
    /// these types that DO open a path are listed in <see cref="IsBannedMember"/> (<c>StreamReader(string path)</c>,
    /// <c>StreamWriter(string path)</c>). <c>File</c>, <c>FileStream</c>, <c>Directory</c>, <c>Path</c>,
    /// <c>FileInfo</c>, pipes, memory-mapped files, <c>UnmanagedMemoryStream</c> and
    /// <c>System.IO.Compression.ZipFile</c> stay banned. The compression types additionally need their assembly
    /// (<c>System.IO.Compression</c>) in the allow-list or the mapping's grant.
    /// </summary>
    public static readonly IReadOnlySet<string> MandatoryAllowedTypes = new HashSet<string>(System.StringComparer.Ordinal)
    {
        "System.IO.Stream",
        "System.IO.MemoryStream",
        "System.IO.BufferedStream",
        "System.IO.BinaryReader",
        "System.IO.BinaryWriter",
        "System.IO.TextReader",
        "System.IO.TextWriter",
        "System.IO.StringReader",
        "System.IO.StringWriter",
        "System.IO.StreamReader",
        "System.IO.StreamWriter",
        "System.IO.SeekOrigin",
        "System.IO.IOException",
        "System.IO.EndOfStreamException",
        "System.IO.InvalidDataException",
        "System.IO.Compression.GZipStream",
        "System.IO.Compression.DeflateStream",
        "System.IO.Compression.BrotliStream",
        "System.IO.Compression.ZLibStream",
        "System.IO.Compression.CompressionLevel",
        "System.IO.Compression.CompressionMode",
        "System.IO.Compression.ZipArchive",
        "System.IO.Compression.ZipArchiveEntry",
        "System.IO.Compression.ZipArchiveMode",
    };

    /// <summary>
    /// Member-level bans that apply whatever the namespace: the routes from an allowed type to the file system, the
    /// process, or a banned type obtained by name — each one would otherwise let a script reach what the namespace
    /// ban forbids (a reflection object can be invoked through <c>dynamic</c>, which leaves no symbol behind).
    /// Measured against every shipped domain package before banning (2026-10-31): none of these is used. Kept on
    /// purpose because shipped packages use them: <c>Environment.GetEnvironmentVariable</c> (vnext-sys-flow),
    /// <c>Type.GetProperty</c>, <c>Type.Assembly</c>, <c>AppDomain</c>, <c>AssemblyLoadContext</c> (morph-idm).
    /// </summary>
    internal static string? IsBannedMember(ISymbol? symbol)
    {
        // Cheap name test first: the analyzer binds every name in the script, and a display string is not free.
        if (symbol?.ContainingType is not { Name: "StreamReader" or "StreamWriter" or "Activator" or "Type" or "Environment" } containing)
            return null;
        var type = containing.OriginalDefinition.ToDisplayString();
        switch (type)
        {
            // A path-taking constructor of an allowed stream type opens the file.
            case "System.IO.StreamReader" or "System.IO.StreamWriter"
                when symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
                     && ctor.Parameters.Any(p => p.Name == "path"):
                return $"{type}(string path)";

            // Any type, an allowed one included, instantiated by Type object; CreateInstance<T>() names T and is checked.
            case "System.Activator" when symbol is IMethodSymbol { IsGenericMethod: false }:
                return $"System.Activator.{symbol.Name}";

            // A type by name, and the reflection objects that invoke members through dynamic.
            case "System.Type" when symbol.Name is "GetType" && symbol.IsStatic:
            case "System.Type" when symbol.Name is "GetTypeFromProgID" or "GetTypeFromCLSID" or "InvokeMember"
                or "GetMethod" or "GetMethods" or "GetConstructor" or "GetConstructors" or "GetMember" or "GetMembers"
                or "GetField" or "GetFields" or "GetEvent" or "GetEvents" or "FindMembers" or "GetDefaultMembers"
                or "TypeInitializer":
                return $"System.Type.{symbol.Name}";

            // Process termination and environment writes (reads stay allowed: shipped system flows use them).
            case "System.Environment" when symbol.Name is "Exit" or "FailFast" or "SetEnvironmentVariable":
                return $"System.Environment.{symbol.Name}";

            default:
                return null;
        }
    }

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

                        // Member-level bans first: they apply inside allowed namespaces and allowed types too.
                        if (IsBannedMember(symbol) is { } bannedMember)
                        {
                            var memberLine = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            violations.Add(
                                $"{Path.GetFileName(tree.FilePath)}({memberLine}): banned member '{bannedMember}' via '{symbol}'");
                            break;
                        }

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
            return verdict is null || IsCarvedOut(type) ? null : verdict;

        var name = ns.ToDisplayString();

        verdict = allowedCarveOuts.Any(a => name == a || name.StartsWith(a + ".", System.StringComparison.Ordinal))
            ? null
            : banned.FirstOrDefault(b => name == b || name.StartsWith(b + ".", System.StringComparison.Ordinal));

        namespaceVerdicts[ns] = verdict;
        return verdict is null || IsCarvedOut(type) ? null : verdict;
    }

    /// <summary>
    /// A carved-out type (and its members) is allowed under a banned namespace; its own path-opening members are caught
    /// by <see cref="IsBannedMember"/> first. Only reached on a banned-namespace hit, so the display string is rare.
    /// </summary>
    private static bool IsCarvedOut(ITypeSymbol type)
        => MandatoryAllowedTypes.Contains(type.OriginalDefinition.ToDisplayString());
}
