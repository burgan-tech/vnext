using BBT.Aether.MultiSchema;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Functions;
using BBT.Workflow.Scripting.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using BBT.Workflow.Definitions.Timer;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Implementation of the script engine that provides C# script evaluation and compilation capabilities.
/// Integrates with Dapr for distributed computing scenarios and provides global functions for scripts.
/// Uses Roslyn's scripting APIs for dynamic C# code execution.
/// Automatically injects services into ScriptBase instances for DI-compatible scripting.
/// </summary>
/// <param name="evaluator">The underlying C# evaluator responsible for script compilation and execution (injected as singleton for shared caching)</param>
/// <param name="scriptServices">The script services to inject into compiled script instances</param>
/// <param name="workflowMetrics">The workflow metrics service for recording script engine metrics</param>
/// <param name="helperRegistry">Registry that compiles and caches referenced helper sets</param>
/// <param name="helpersOptions">Feature switch for the custom-script-helpers capability</param>
/// <param name="serviceProvider">Used to lazily resolve helper-only dependencies (component store, schema)
/// so non-helper compiles never construct the cache graph</param>
/// <param name="logger">Logger for helper/sandbox diagnostics</param>
public sealed class ScriptEngine(
    IEvaluator evaluator,
    IScriptServices scriptServices,
    IWorkflowMetrics workflowMetrics,
    IScriptHelperRegistry helperRegistry,
    ScriptHelpersOptions helpersOptions,
    IServiceProvider serviceProvider,
    ILogger<ScriptEngine> logger) : IScriptEngine
{
    /// <summary>
    /// The underlying C# evaluator responsible for script compilation and execution.
    /// Injected as a singleton to share the script cache across all requests.
    /// </summary>
    private readonly IEvaluator _evaluator = evaluator;

    /// <summary>
    /// The script services to inject into compiled script instances.
    /// Provides access to Dapr, logging, and configuration.
    /// </summary>
    private readonly IScriptServices _scriptServices = scriptServices;

    private readonly IScriptHelperRegistry _helperRegistry = helperRegistry;
    private readonly ScriptHelpersOptions _helpersOptions = helpersOptions;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ILogger<ScriptEngine> _logger = logger;

    /// <summary>
    /// Lazily-initialized default metadata references used for script compilation.
    /// Includes core .NET types, collections, and workflow-specific assemblies.
    /// </summary>
    private static readonly Lazy<MetadataReference[]> DefaultReferences = new(() =>
    {
        var references = new List<MetadataReference?>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(TimerSchedule).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Dictionary<,>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo).Assembly.Location),
            // System.Linq.Expressions: provides DynamicAttribute and System.Dynamic.ExpandoObject, both
            // required for the `dynamic` keyword that mappings rely on (e.g. Handler returns dynamic,
            // ScriptContext.Body is dynamic). Runtime-owned so it is always referenced even under the
            // sandbox, where the curated assembly allow-list would otherwise omit it.
            MetadataReference.CreateFromFile(typeof(System.Dynamic.ExpandoObject).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ScriptBase).Assembly.Location),
            // BBT.Aether.Domain: aggregate base types (AggregateRoot<>, Entity<>, audit interfaces).
            // Domain entities exposed to mappings — e.g. ScriptContext.Instance — inherit members such as
            // Id/CreationTime from here, so the assembly must be referenced for those inherited members to
            // resolve. Like the others, its simple name flows into the sandbox grant automatically.
            MetadataReference.CreateFromFile(typeof(BBT.Aether.Domain.Entities.AggregateRoot<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(JsonSerializableAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Text.Encodings.Web.JavaScriptEncoder).Assembly.Location),
            // System.Private.Uri: the runtime implementation of System.Uri. The System.Runtime facade
            // only type-forwards Uri here, so without this reference any mapping calling
            // Uri.EscapeDataString — ubiquitous in domain scripts for query-string composition — fails
            // with CS0103 under the sandbox. URI parsing/encoding only; networking types live in
            // System.Net.* and stay blocked by the mandatory banned-namespace baseline.
            MetadataReference.CreateFromFile(typeof(Uri).Assembly.Location),
            // System.Private.Xml / System.Private.Xml.Linq: the runtime *implementations* of XmlDocument
            // and XDocument. Necessary but NOT sufficient — see the facade references below.
            MetadataReference.CreateFromFile(typeof(System.Xml.XmlDocument).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Xml.Linq.XDocument).Assembly.Location),
            // XML facade/contract assemblies. Task definitions compiled against the .NET ref pack bind
            // XmlDocument/XDocument to the facade identities (System.Xml.ReaderWriter / System.Xml.XDocument),
            // not the System.Private.Xml* implementations above. Roslyn resolves type references by
            // assembly identity, so any script that merely *touches* such a type — e.g. SoapTask, whose
            // SetBody(XmlDocument) overload forces resolution of every member signature — fails with
            // CS0012 unless the facade itself is referenced. The facade has no managed type of its own
            // (it only forwards to System.Private.Xml), so it cannot be reached via typeof(...).Assembly
            // and must be resolved by name from the trusted-platform-assembly set. Their simple names
            // flow into the sandbox grant automatically via DefaultReferenceAssemblyNames.
            ResolvePlatformFacade("System.Xml.ReaderWriter"),
            ResolvePlatformFacade("System.Xml.XDocument"),
        };

        return references.Where(r => r is not null).Cast<MetadataReference>().ToArray();
    });

    /// <summary>
    /// Resolves a platform facade/contract assembly (one that carries only <c>TypeForwardedTo</c>
    /// entries and no managed types, so it cannot be reached through <c>typeof(...).Assembly</c>) by
    /// its simple name from the runtime's trusted-platform-assembly list. Returns <c>null</c> when the
    /// facade is absent on the host runtime, letting the caller degrade gracefully instead of failing
    /// engine startup.
    /// </summary>
    private static MetadataReference? ResolvePlatformFacade(string simpleName)
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var path = Array.Find(tpa, p =>
            string.Equals(System.IO.Path.GetFileNameWithoutExtension(p), simpleName, StringComparison.OrdinalIgnoreCase));

        return path is null ? null : MetadataReference.CreateFromFile(path);
    }

    /// <summary>
    /// Simple assembly names backing <see cref="DefaultReferences"/>. These are merged into the sandbox
    /// grant on every compile so the engine's own default references are always part of the effective
    /// allow-list when the sandbox is enabled — existing mappings keep compiling without operators
    /// having to restate them in <c>Scripting:Sandbox:AllowedAssemblies</c>.
    /// </summary>
    private static readonly Lazy<string[]> DefaultReferenceAssemblyNames = new(() =>
        DefaultReferences.Value
            .Select(r => System.IO.Path.GetFileNameWithoutExtension(r.Display) ?? string.Empty)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());

    /// <summary>
    /// Default using directives automatically included in all scripts.
    /// Provides access to common .NET namespaces and workflow-specific types.
    /// </summary>
    private static readonly string[] DefaultUsings =
    {
        "System",
        "System.Linq",
        "System.Collections.Generic",
        "System.Threading",
        "System.Threading.Tasks",
        "System.Dynamic",
        "System.Text.Json",
        "System.Text.Json.Serialization",
        "System.Text.Encodings.Web",
        "System.Text.Unicode",
        "BBT.Workflow.Shared",
        "BBT.Workflow.Scripting",
        "BBT.Workflow.Definitions",
        "BBT.Workflow.Instances",
        "BBT.Workflow.Filtering",
        "BBT.Workflow.Runtime",
        "BBT.Workflow.Scripting.Functions",
        "BBT.Workflow.Definitions.Timer",
        "System.Xml",
        "System.Xml.Linq",
        // System.Security: brings SecurityElement.Escape into scope for XML/SOAP-safe escaping of
        // user input. The root namespace is benign (SecurityElement/SecureString/legacy CAS no-ops);
        // a using grants no new capability — the sandbox boundary stays the reference allow-list +
        // BannedApiAnalyzer, and dangerous sub-namespaces (Cryptography/Principal) are NOT imported.
        "System.Security"
    };

    /// <summary>
    /// Per-evaluator table of no-helper compile profiles, keyed by effective-grant identity (see
    /// <see cref="GrantKeyOf"/>). Every no-helper compile with the same grant shares one profile — the
    /// reference/using set the engine itself builds never varies otherwise. Bounded by design: the set
    /// of distinct authored <c>AllowedAssemblies</c> grant lists is definition-config bounded (workflow
    /// and task definitions), not request-bounded, so this dictionary cannot grow unbounded under a
    /// request-driven workload.
    /// <para>
    /// Outer-keyed by <see cref="IEvaluator"/> instance (via <see cref="ConditionalWeakTable{TKey,TValue}"/>)
    /// rather than a single process-wide table: a <see cref="IEvaluator.BuildProfile"/> result bakes in that
    /// specific evaluator's sandbox flag (<c>sbx:0</c>/<c>sbx:1</c>). Two <see cref="ScriptEngine"/>
    /// instances wrapping differently-configured evaluators in one process must not share entries, or a
    /// profile built against one evaluator's sandbox setting could precompute a key for a compile that
    /// actually runs against the other's — this also keeps test instances (each with their own evaluator)
    /// from leaking state into one another.
    /// </para>
    /// </summary>
    private static readonly ConditionalWeakTable<IEvaluator, ConcurrentDictionary<string, string>> BaseProfilesByEvaluator = new();

    /// <summary>
    /// Per-evaluator table of helper-set compile profiles, keyed by the helper set's compiled
    /// <see cref="MetadataReference"/> identity. Keyed on the reference rather than the owning
    /// <see cref="HelperSet"/> record: <see cref="ScriptHelperRegistry"/> returns a fresh
    /// <c>with { FromCache = true }</c> copy on every cache hit, which would defeat a
    /// <see cref="HelperSet"/>-keyed table on every hit — the <see cref="MetadataReference"/> instance
    /// itself is never copied, so it is the stable identity. Safe against a same-reference/different-grant
    /// collision: the registry's own cache key already folds the grant into (helpers + allowedAssemblies),
    /// so the same <see cref="HelperSet.Reference"/> object can only ever have been built for one grant;
    /// the profile also includes the grant again as a second, independent guard. Outer-keyed by
    /// <see cref="IEvaluator"/> for the same reason as <see cref="BaseProfilesByEvaluator"/>.
    /// </summary>
    private static readonly ConditionalWeakTable<IEvaluator, ConditionalWeakTable<MetadataReference, string>> HelperProfilesByEvaluator = new();

    /// <summary>
    /// <see cref="MergeDefaultGrant"/> result memoized per authored grant-list object. Definition
    /// objects are replaced whole on every publish, so keying on object identity here can never serve a
    /// stale merge for an updated definition — see the "authored ref tuple is not identity" principle.
    /// </summary>
    private static readonly ConditionalWeakTable<IReadOnlyList<string>, IReadOnlyList<string>> MergedGrants = new();

    /// <summary>
    /// The component type key mappings/helpers are published under — identical to
    /// <see cref="BBT.Workflow.Definitions.Mapping.ComponentTypeKey"/> and to the value the mapping
    /// <c>CacheSet&lt;Mapping&gt;</c> itself passes to <see cref="IComponentGenerationProvider"/> when it
    /// reads/bumps a generation token. <see cref="HelperSetMemoByEvaluator"/>'s guard MUST read the
    /// identical (componentTypeKey, domain, key) triple that publish bumps, or the memo would never see
    /// the invalidation.
    /// </summary>
    private const string MappingsTypeKey = RuntimeSysSchemaInfo.Mappings;

    /// <summary>
    /// Helper-set resolution result cached by <see cref="HelperSetMemoByEvaluator"/>: the resolved sources,
    /// the per-helper generation tokens they were resolved under, and the built <see cref="HelperSet"/>.
    /// </summary>
    private sealed record ResolvedHelperSet(
        IReadOnlyList<HelperSource> Sources,
        string[] Tokens,
        HelperSet Set);

    /// <summary>
    /// Per-evaluator helper-set resolution memo, guarded by component generation tokens (A7). The
    /// authored reference tuple (domain/key/version) is NEVER the cache identity by itself — a version
    /// resolves floating (see <see cref="IComponentGenerationProvider"/>'s remarks), so a hotfix publish
    /// that keeps the authored version reachable must still be visible on the next compile. Every use
    /// re-reads the per-helper generation tokens under <see cref="MappingsTypeKey"/>, and any mismatch
    /// forces a full re-resolve through the component store — the token read is the guard, not an
    /// optimization detail. Keyed per (authored refs + grant) string. Outer-keyed by <see cref="IEvaluator"/>
    /// for the same isolation reason as <see cref="BaseProfilesByEvaluator"/>/<see cref="HelperProfilesByEvaluator"/>.
    /// Bounded by design: entries grow with distinct authored helper-ref-list + grant combinations —
    /// definition-config bounded, not request-bounded (same reasoning as the sibling profile tables).
    /// </summary>
    private static readonly ConditionalWeakTable<IEvaluator, ConcurrentDictionary<string, ResolvedHelperSet>> HelperSetMemoByEvaluator = new();

    /// <summary>
    /// Stable string key for a per-compile sandbox grant, used to index <see cref="BaseProfilesByEvaluator"/>.
    /// Order-insensitive, case-insensitive — mirrors <see cref="CSharpEvaluator.BuildProfile"/>'s own
    /// grant ordering so two grant lists that would produce the same profile also share one cache slot.
    /// </summary>
    private static string GrantKeyOf(IReadOnlyList<string>? grant) =>
        grant is null or { Count: 0 } ? "" : string.Join("|", grant.OrderBy(g => g, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Memoized wrapper over <see cref="MergeDefaultGrant"/>: the merge is pure given a stable grant
    /// object, so repeating it on every compile buys nothing once the grant's owning definition is
    /// unchanged.
    /// </summary>
    private static IReadOnlyList<string> MergedGrantOf(IReadOnlyList<string>? grant)
        => grant is null or { Count: 0 }
            ? DefaultReferenceAssemblyNames.Value
            : MergedGrants.GetValue(grant, static g => MergeDefaultGrant(g));

    /// <summary>
    /// Merges caller-supplied extra references with the engine defaults — the SAME expression
    /// <see cref="CompileCoreAsync{T}"/> feeds the evaluator, factored out so a precomputed profile can
    /// never diverge from what the evaluator actually compiles against.
    /// </summary>
    private static IEnumerable<MetadataReference> MergedRefs(IEnumerable<MetadataReference>? extraReferences)
        => (extraReferences ?? []).Concat(DefaultReferences.Value).Distinct();

    /// <summary>
    /// Merges caller-supplied extra usings with the engine defaults — the SAME expression
    /// <see cref="CompileCoreAsync{T}"/> feeds the evaluator, factored out for the same reason as
    /// <see cref="MergedRefs"/>.
    /// </summary>
    private static IEnumerable<string> MergedUsings(IEnumerable<string>? usingDirectives)
        => (usingDirectives ?? []).Concat(DefaultUsings).Distinct();

    /// <summary>
    /// SHA-256 hex identity of a resolved mapping body. REF-encoded <see cref="ScriptCode"/> instances
    /// share the empty-string <see cref="ScriptCode.ContentHash"/> (their <see cref="ScriptCode.DecodedCode"/>
    /// is empty by design), so per-source identity must hash the resolved body instead — exactly as the
    /// evaluator's own <c>GenerateCacheKey</c> would for the raw-string path.
    /// </summary>
    private static string SourceHashOf(ScriptCode scriptCode, string body) =>
        scriptCode.IsReference
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
            : scriptCode.ContentHash;

    /// <summary>
    /// Compiles C# code into an instance of the specified type asynchronously.
    /// Automatically includes default metadata references and using directives,
    /// merging them with any additional references and usings provided.
    /// Services are automatically injected into ScriptBase instances.
    /// </summary>
    /// <typeparam name="T">The target type to compile the code into</typeparam>
    /// <param name="code">The C# code to compile</param>
    /// <param name="extraReferences">Optional additional metadata references for compilation</param>
    /// <param name="usingDirectives">Optional additional using directives to include</param>
    /// <param name="cancellationToken">
    /// Gates entry only: honoured before a compile is looked up/started, not once one is under way. A
    /// compile for a given cache key is shared by every caller waiting on it, so it is not cancellable
    /// mid-flight by any single caller's token.
    /// </param>
    /// <returns>A task containing the compiled instance of type T</returns>
    /// <exception cref="CompilationErrorException">Thrown when the code contains compilation errors</exception>
    /// <exception cref="InvalidOperationException">Thrown when the code cannot be compiled to the target type</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is already cancelled at entry</exception>
    public Task<T> CompileToInstanceAsync<T>(
        string code,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default)
    {
        return CompileCoreAsync<T>(code, extraReferences, usingDirectives, null, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<T> CompileToInstanceAsync<T>(
        ScriptCode scriptCode,
        ScriptSettings? flowScripts = null,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scriptCode);

        // Flow-level scripts are global to the workflow; union them with the task/mapping-level scripts.
        // NOTE: deliberately NOT .ToArray()'d — Union returns one of its inputs UNCHANGED when either
        // side is null (the common single-source case), so keeping the authored IReadOnlyList<string>
        // object as-is lets MergedGrantOf's ConditionalWeakTable key on that stable definition object
        // instead of missing on a fresh copy every single call.
        var effective = ScriptSettings.Union(flowScripts, scriptCode.Scripts);
        var grant = effective?.AllowedAssemblies;

        // Resolve the mapping body: for REF encoding, fetch it from the sys-mappings component store;
        // otherwise use the inline (Native/Base64) code.
        var body = scriptCode.IsReference
            ? await ResolveReferencedCodeAsync(scriptCode.CodeReference!, cancellationToken)
            : scriptCode.DecodedCode;

        // A precomputed cache key is only valid when THIS method fully controls the reference/using set
        // CompileCoreAsync will feed the evaluator. A caller-supplied extraReferences/usingDirectives
        // (the public API allows both) changes that set, and a profile built without them would diverge
        // from the key CompileCoreAsync's own merge produces — so any caller-supplied value here falls
        // through to the untouched raw (per-call) key algorithm below.
        var engineControlsInputs = extraReferences is null && usingDirectives is null;

        // No helpers declared → straight compile (effective sandbox grant still applies).
        if (effective?.HasHelpers != true)
        {
            if (engineControlsInputs)
            {
                // Profile depends only on the grant here (references/usings are always the engine's own
                // defaults) — one profile per distinct grant, reused across every no-helper compile
                // against THIS evaluator.
                var baseProfiles = BaseProfilesByEvaluator.GetValue(
                    _evaluator, static _ => new ConcurrentDictionary<string, string>());
                var profile = baseProfiles.GetOrAdd(GrantKeyOf(grant), _ =>
                    _evaluator.BuildProfile(MergedRefs(null), MergedUsings(null), MergedGrantOf(grant), loadContext: null));

                var sourceHash = SourceHashOf(scriptCode, body);
                var key = _evaluator.ComputeCacheKey(sourceHash, typeof(T), profile);

                return await CompileCoreAsync<T>(
                    body, null, null, null, grant, cancellationToken, precomputedCacheKey: key);
            }

            return await CompileCoreAsync<T>(
                body, extraReferences, usingDirectives, null, grant, cancellationToken);
        }

        if (!_helpersOptions.Enabled)
        {
            _logger.ScriptHelpersDisabled();
            throw new InvalidOperationException(
                "Mapping references helpers but the custom-script-helpers feature is disabled " +
                "(Scripting:Helpers:Enabled=false).");
        }

        var helperKeys = string.Join(", ", effective.Helpers!.Select(h => $"{h.Key}@{h.Version}"));

        // A7 guard: re-read the per-helper generation tokens BEFORE consulting the memo. The tokens are
        // the guard itself, not a hint — a mismatch (hotfix publish) must force the full resolve path
        // below even though the authored ref list and grant are unchanged.
        var memoKey = string.Join("|", effective.Helpers!.Select(h => h.ToString())) + "||" + GrantKeyOf(grant);
        var generationProvider = _serviceProvider.GetRequiredService<IComponentGenerationProvider>();

        // Reads run in parallel: this loop is on EVERY helper'lı compile (hit included), and with
        // Redis-backed tokens each read is a network round-trip — N sequential RTTs would sit on the
        // hot path. Positional indexing keeps token order aligned with the authored helper order.
        var tokens = await Task.WhenAll(effective.Helpers!.Select(h =>
            generationProvider.GetAsync(MappingsTypeKey, h.Domain, h.Key, cancellationToken)));

        var helperSetMemo = HelperSetMemoByEvaluator.GetValue(
            _evaluator, static _ => new ConcurrentDictionary<string, ResolvedHelperSet>());

        HelperSet helperSet;
        IReadOnlyList<HelperSource> helperSources;
        if (helperSetMemo.TryGetValue(memoKey, out var memo) && memo.Tokens.AsSpan().SequenceEqual(tokens))
        {
            // Memo hit: neither the component store nor the helper registry is touched, only N cheap
            // token comparisons. Reuses the memoized HelperSet instance verbatim (not a `with` copy) so
            // its Reference identity stays stable for HelperProfilesByEvaluator's CWT lookup below.
            helperSources = memo.Sources;
            helperSet = memo.Set;
            _logger.ScriptHelperSetCacheHit(helperSources.Count, helperKeys);
        }
        else
        {
            helperSources = await ResolveHelperSourcesAsync(effective.Helpers!, cancellationToken);

            // Build the referenced helper set first (sandboxed, cached by content hash), referencing the
            // runtime-owned contract assemblies and importing the default namespaces.
            helperSet = _helperRegistry.GetOrBuildHelpers(
                helperSources,
                MergeDefaultGrant(grant),
                DefaultReferences.Value,
                DefaultUsings,
                cancellationToken);

            helperSetMemo[memoKey] = new ResolvedHelperSet(helperSources, tokens, helperSet with { FromCache = false });

            if (helperSet.FromCache)
            {
                _logger.ScriptHelperSetCacheHit(helperSources.Count, helperKeys);
            }
            else
            {
                _logger.ScriptHelperSetBuilt(helperSources.Count, helperKeys, string.Join(", ", helperSet.Namespaces));
            }
        }

        // Reference the helper assembly + auto-import its namespaces, and compile the mapping into the
        // helper set's load context so helper types resolve at runtime.
        var refs = (extraReferences ?? []).Append(helperSet.Reference);
        var usings = (usingDirectives ?? []).Concat(helperSet.Namespaces);

        if (engineControlsInputs)
        {
            // Keyed on the Reference object itself (see HelperProfilesByEvaluator's doc comment): stable
            // across the registry's `with { FromCache = true }` copies, unlike the owning HelperSet
            // record. Built from the exact same MergedRefs/MergedUsings calls CompileCoreAsync below will
            // make with these exact `refs`/`usings` values, so the profile can never diverge from what
            // the evaluator actually compiles against.
            var helperProfiles = HelperProfilesByEvaluator.GetValue(
                _evaluator, static _ => new ConditionalWeakTable<MetadataReference, string>());
            var helperProfile = helperProfiles.GetValue(helperSet.Reference, _ =>
                _evaluator.BuildProfile(MergedRefs(refs), MergedUsings(usings), MergedGrantOf(grant), helperSet.LoadContext));

            var sourceHash = SourceHashOf(scriptCode, body);
            var key = _evaluator.ComputeCacheKey(sourceHash, typeof(T), helperProfile);

            return await CompileCoreAsync<T>(
                body, refs, usings, helperSet.LoadContext, grant, cancellationToken, precomputedCacheKey: key);
        }

        return await CompileCoreAsync<T>(
            body, refs, usings, helperSet.LoadContext, grant, cancellationToken);
    }

    /// <summary>
    /// Resolves a <c>REF</c>-encoded mapping body from the sys-mappings component store. The referenced
    /// component is plain (Native/Base64) — no REF chaining.
    /// </summary>
    private async Task<string> ResolveReferencedCodeAsync(Reference reference, CancellationToken cancellationToken)
    {
        var componentCacheStore = _serviceProvider.GetRequiredService<IComponentCacheStore>();
        var currentSchema = _serviceProvider.GetRequiredService<ICurrentSchema>();

        using (currentSchema.Change(RuntimeSysSchemaInfo.Mappings))
        {
            var result = await componentCacheStore.GetMappingAsync(
                reference.Domain, reference.Key, reference.Version, cancellationToken);

            if (!result.IsSuccess || result.Value is null)
            {
                _logger.ScriptHelperReferenceUnresolved(reference.Domain, reference.Flow, reference.Key, reference.Version);
                throw new InvalidOperationException(
                    $"Referenced mapping component could not be resolved: {reference}");
            }

            return result.Value.DecodedCode;
        }
    }

    /// <summary>
    /// Resolves the referenced helper components from the component store (under the sys-mappings schema)
    /// into compilable sources, ordered as declared.
    /// </summary>
    private async Task<IReadOnlyList<HelperSource>> ResolveHelperSourcesAsync(
        IReadOnlyList<Reference> helpers,
        CancellationToken cancellationToken)
    {
        var sources = new List<HelperSource>(helpers.Count);

        // Resolve helper-only dependencies lazily so non-helper compiles never touch the cache graph.
        var componentCacheStore = _serviceProvider.GetRequiredService<IComponentCacheStore>();
        var currentSchema = _serviceProvider.GetRequiredService<ICurrentSchema>();

        using (currentSchema.Change(RuntimeSysSchemaInfo.Mappings))
        {
            foreach (var helper in helpers)
            {
                var result = await componentCacheStore.GetMappingAsync(
                    helper.Domain, helper.Key, helper.Version, cancellationToken);

                if (!result.IsSuccess || result.Value is null)
                {
                    _logger.ScriptHelperReferenceUnresolved(helper.Domain, helper.Flow, helper.Key, helper.Version);
                    throw new InvalidOperationException(
                        $"Helper component could not be resolved: {helper.Domain}/{helper.Flow}/{helper.Key}/{helper.Version}");
                }

                var mapping = result.Value;
                sources.Add(new HelperSource(
                    mapping.Key,
                    mapping.Version,
                    mapping.DecodedCode,
                    $"{mapping.Key}.csx"));
            }
        }

        return sources;
    }

    /// <summary>
    /// Merges the engine's default reference assembly names into the per-compile sandbox grant so the
    /// runtime's own references are always part of the effective allow-list under the sandbox.
    /// </summary>
    private static IReadOnlyList<string> MergeDefaultGrant(IReadOnlyList<string>? grant)
    {
        if (grant is null || grant.Count == 0)
            return DefaultReferenceAssemblyNames.Value;

        return grant
            .Concat(DefaultReferenceAssemblyNames.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<T> CompileCoreAsync<T>(
        string code,
        IEnumerable<MetadataReference>? extraReferences,
        IEnumerable<string>? usingDirectives,
        AssemblyLoadContext? loadContext,
        IReadOnlyList<string>? sandboxGrant,
        CancellationToken cancellationToken,
        string? precomputedCacheKey = null)
    {
        var stopwatch = Stopwatch.StartNew();
        const string scriptType = "compilation";
        const string language = "csharp";

        try
        {
            // Same merge expressions a precomputed profile is built from (MergedRefs/MergedUsings) —
            // factored out so the two can never diverge.
            var mergedReferences = MergedRefs(extraReferences);
            var mergedUsings = MergedUsings(usingDirectives);

            // Pass script services to the evaluator for injection into ScriptBase instances
            var compilation = await _evaluator.CompileToInstanceAsync<T>(
                code,
                _scriptServices,
                mergedReferences,
                mergedUsings,
                cancellationToken,
                loadContext,
                MergeDefaultGrant(sandboxGrant),
                precomputedCacheKey);

            stopwatch.Stop();
            var durationSeconds = stopwatch.Elapsed.TotalSeconds;
            var cache = compilation.Compiled ? "miss" : "hit";

            // DEPRECATED (vnext-meta/deprecations.json): script_executions_total keeps its historical
            // compile-path semantics until consumers migrate — do not remove or repurpose here.
            workflowMetrics.RecordScriptExecution(scriptType, language, "success");
            workflowMetrics.RecordScriptCompilation(cache, "success");
            workflowMetrics.RecordScriptCompilationDuration(scriptType, language, "success", durationSeconds, cache);
            // The type cache never evicts, so its size only changes on a miss; skipping the gauge on
            // hits avoids ConcurrentDictionary.Count's all-stripe lock on the hot path.
            if (compilation.Compiled)
            {
                workflowMetrics.SetCacheEntries("script-types", _evaluator.CachedTypeCount);
            }

            return compilation.Instance;
        }
        catch (CompilationErrorException ex)
        {
            stopwatch.Stop();
            var durationSeconds = stopwatch.Elapsed.TotalSeconds;

            // Record compilation error
            workflowMetrics.RecordScriptExecution(scriptType, language, "compilation_error");
            workflowMetrics.RecordScriptCompilation("miss", "compilation_error");
            workflowMetrics.RecordScriptCompilationError(scriptType, language, ex.GetType().Name);
            workflowMetrics.RecordScriptCompilationDuration(scriptType, language, "compilation_error", durationSeconds, "miss");

            throw;
        }
        catch (InvalidOperationException ex)
        {
            stopwatch.Stop();
            var durationSeconds = stopwatch.Elapsed.TotalSeconds;

            // Record invalid operation as compilation error
            workflowMetrics.RecordScriptExecution(scriptType, language, "invalid_operation");
            workflowMetrics.RecordScriptCompilation("miss", "invalid_operation");
            workflowMetrics.RecordScriptCompilationError(scriptType, language, ex.GetType().Name);
            workflowMetrics.RecordScriptCompilationDuration(scriptType, language, "invalid_operation", durationSeconds, "miss");

            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var durationSeconds = stopwatch.Elapsed.TotalSeconds;

            // Record cancelled compilation
            workflowMetrics.RecordScriptExecution(scriptType, language, "cancelled");
            // A failing call by definition did not come from the cache; OperationCanceledException can
            // fire before lookup, but counting it as a miss is an accepted simplification.
            workflowMetrics.RecordScriptCompilation("miss", "cancelled");
            workflowMetrics.RecordScriptCompilationDuration(scriptType, language, "cancelled", durationSeconds, "miss");

            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var durationSeconds = stopwatch.Elapsed.TotalSeconds;

            // Record unexpected compilation error
            workflowMetrics.RecordScriptExecution(scriptType, language, "unexpected_error");
            workflowMetrics.RecordScriptCompilation("miss", "unexpected_error");
            workflowMetrics.RecordScriptCompilationError(scriptType, language, ex.GetType().Name);
            workflowMetrics.RecordScriptCompilationDuration(scriptType, language, "unexpected_error", durationSeconds, "miss");

            throw;
        }
    }
}
