using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Functions;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BBT.Workflow.Scripting.Evaluators;

/// <summary>
/// Memory-safe C# script evaluator with assembly sharing.
/// Caches compiled Types so the same script code reuses the same assembly.
/// Uses collectible AssemblyLoadContext for proper memory management.
///
/// When a <see cref="ScriptSandboxOptions"/> with <c>Enabled = true</c> is supplied, all compilations
/// use the restricted reference set (<see cref="SandboxedReferenceSet"/>) instead of scanning the
/// whole AppDomain, and the <see cref="BannedApiAnalyzer"/> runs before IL emission.
/// </summary>
public class CSharpEvaluator : IEvaluator
{
    private readonly ScriptSandboxOptions _sandbox;

    /// <summary>
    /// Creates a new evaluator. When <paramref name="sandboxOptions"/> is null or has
    /// <c>Enabled = false</c>, the legacy (non-sandboxed) compile path is used.
    /// </summary>
    public CSharpEvaluator(ScriptSandboxOptions? sandboxOptions = null)
    {
        _sandbox = sandboxOptions ?? new ScriptSandboxOptions { Enabled = false };
    }

    /// <summary>
    /// Cached compiled scripts indexed by cache key.
    ///
    /// The value is a <see cref="Lazy{T}"/> so concurrent callers with the same key compile exactly
    /// once. This is a correctness requirement, not an optimisation: the assembly's simple name is
    /// derived from the cache key, and an <see cref="AssemblyLoadContext"/> cannot hold two
    /// assemblies with the same simple name, so a second concurrent load into a shared helper
    /// context throws.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<CompiledScript>> _typeCache = new();

    /// <summary>
    /// Cached metadata references - created once and reused for all compilations.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<MetadataReference>> DefaultMetadataReferences = new(
        CreateDefaultReferences, 
        LazyThreadSafetyMode.ExecutionAndPublication);
    
    /// <summary>
    /// Parse options for C# 12 language features (collection expressions, etc.)
    /// </summary>
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12);

    /// <summary>
    /// Assigns each shared <see cref="AssemblyLoadContext"/> a stable scope id, on first use, so the
    /// cache key can distinguish compiles into different contexts without a second parameter that
    /// could disagree with the load context itself (see <see cref="GetCacheScope"/>) —
    /// the API previously took an explicit <c>cacheScope</c> string, and nothing stopped a caller from
    /// passing a scope that named one context while compiling into another. Keyed weakly: the table
    /// must never be what keeps a context alive — <see cref="CompiledScript.Context"/>, held strongly
    /// by <see cref="_typeCache"/>, already does that.
    ///
    /// Consequence worth knowing: because <see cref="_typeCache"/> pins every <c>CompiledScript</c> for
    /// the singleton's lifetime, a superseded helper load context — and every assembly loaded into
    /// it — is never collected either. It, and this table's entry for it, are retained for the process
    /// lifetime, not stranded. A retained context is the correct trade against ever serving a compile
    /// from the wrong one, which is what the withdrawn content-hash design risked.
    ///
    /// Deliberately <c>static</c>, not per-instance: two <see cref="CSharpEvaluator"/> instances that
    /// share one <see cref="AssemblyLoadContext"/> (the production shape — the evaluator is a
    /// singleton, but tests construct several against one context) must agree on that context's scope
    /// id, or the reuse path in <see cref="CompileAndLoad{T}"/> derives two different cache keys for
    /// the same compilation and silently loads a second copy instead of reusing the first. Scope it to
    /// an instance and <c>CSharpEvaluatorConcurrencyTests.CompileToInstanceAsync_WhenAssemblyAlreadyLoadedInContext_ShouldReuseItInsteadOfThrowing</c>
    /// passes vacuously — each evaluator gets its own scope id, the two never agree, and the test can no
    /// longer tell the difference.
    /// </summary>
    private static readonly ConditionalWeakTable<AssemblyLoadContext, string> LoadContextScopes = new();

    private static long _loadContextScopeSequence;

    /// <summary>
    /// Gets the number of cached script types (unique scripts compiled).
    /// </summary>
    public int CachedTypeCount => _typeCache.Count;

    private long _compileInvocationCount;

    /// <summary>
    /// Test-only observability seam: counts actual calls into <see cref="CompileAndLoad{T}"/> — the
    /// Roslyn emit path — regardless of what the cache or the load context end up holding afterwards.
    /// <see cref="CachedTypeCount"/> and the compiled <see cref="Type"/> identity of a result cannot
    /// distinguish "compiled once" from "compiled N times but the last write won" once the assembly
    /// reuse in <see cref="CompileAndLoad{T}"/> is in play: N redundant concurrent compiles into a
    /// shared context can all resolve to the very same reused assembly without throwing, so those
    /// signals go green even if the <c>Lazy</c> de-duplication in <see cref="CompileToInstanceAsync{T}"/>
    /// regressed away. This counter is the only signal that is not fooled by that.
    /// </summary>
    internal long CompileInvocationCount => Interlocked.Read(ref _compileInvocationCount);

    /// <inheritdoc />
    public Task<EvaluatorCompilation<T>> CompileToInstanceAsync<T>(
        string code,
        IScriptServices? services = null,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default,
        AssemblyLoadContext? loadContext = null,
        IReadOnlyList<string>? sandboxGrant = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Code cannot be null or empty", nameof(code));

        // The caller's token gates entry only. Once a compile starts it is shared by every caller
        // waiting on the same Lazy, so one abandoned request must not fail it — the same rule
        // ScriptHelperRegistry applies to helper-set builds.
        cancellationToken.ThrowIfCancellationRequested();

        // The load context is part of the compilation identity (a type compiled into helper set A's
        // context must never be served to a caller compiling against helper set B), so the scope is
        // derived from loadContext itself rather than taken as a separate parameter.
        var cacheScope = GetCacheScope(loadContext);
        var cacheKey = GenerateCacheKey(
            code, typeof(T), extraReferences, usingDirectives, sandboxGrant, cacheScope);

        // Fast path: an already-materialised entry is the overwhelmingly common case (every script in
        // every transition after the first). Check it before GetOrAdd so the closure below — which
        // captures code/cacheKey/extraReferences/usingDirectives/sandboxGrant/loadContext — is not
        // allocated on every cache hit. Mirrors ScriptHelperRegistry.GetOrBuildHelpers.
        if (_typeCache.TryGetValue(cacheKey, out var existing) && existing.IsValueCreated)
        {
            return Task.FromResult(new EvaluatorCompilation<T>(
                CreateAndInjectServices<T>(existing.Value.CompiledType, services), false, TimeSpan.Zero));
        }

        // The flag/duration live in this call's closure: the factory body runs at most once per cache
        // key (Lazy ExecutionAndPublication), and only the call whose lambda actually created the
        // stored Lazy has its locals written — so exactly one caller reports Compiled=true per emit,
        // no matter which thread triggers materialisation.
        var compiledHere = false;
        var compileDuration = TimeSpan.Zero;
        var lazy = _typeCache.GetOrAdd(cacheKey, _ => new Lazy<CompiledScript>(
            () =>
            {
                var compileTimer = Stopwatch.StartNew();
                var result = CompileAndLoad<T>(code, cacheKey, extraReferences, usingDirectives, sandboxGrant, loadContext);
                compileTimer.Stop();
                compiledHere = true;
                compileDuration = compileTimer.Elapsed;
                return result;
            },
            LazyThreadSafetyMode.ExecutionAndPublication));

        CompiledScript compiled;
        try
        {
            compiled = lazy.Value;
        }
        catch
        {
            // Lazy<T> caches the exception as well as the value, and this evaluator is a singleton:
            // without eviction one transient failure would be replayed for the rest of the process
            // lifetime. Remove only the entry we observed — never one another caller has published.
            _typeCache.TryRemove(new KeyValuePair<string, Lazy<CompiledScript>>(cacheKey, lazy));
            throw;
        }

        return Task.FromResult(new EvaluatorCompilation<T>(
            CreateAndInjectServices<T>(compiled.CompiledType, services), compiledHere, compileDuration));
    }

    /// <inheritdoc />
    public CompiledHelpers CompileHelpers(
        IReadOnlyList<(string Path, string Code)> sources,
        AssemblyLoadContext loadContext,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        IReadOnlyList<string>? sandboxGrant = null,
        CancellationToken cancellationToken = default)
    {
        if (sources is null || sources.Count == 0)
            throw new ArgumentException("At least one helper source is required.", nameof(sources));

        var usingPrefix = BuildUsingPrefix(usingDirectives);
        var trees = sources
            .Select(s => CSharpSyntaxTree.ParseText(
                usingPrefix + s.Code, options: ParseOptions, path: s.Path, cancellationToken: cancellationToken))
            .ToList();

        var assemblyName = $"Helpers_{Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Concat(sources.Select(s => s.Code)))))[..8]}";

        var compilation = CreateCompilation(assemblyName, trees, extraReferences, sandboxGrant);
        RunSandboxAnalyzer(compilation);

        var image = EmitToImage(compilation, cancellationToken);
        var assembly = loadContext.LoadFromStream(new MemoryStream(image));

        var namespaces = assembly.GetExportedTypes()
            .Select(t => t.Namespace)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .Cast<string>()
            .ToArray();

        return new CompiledHelpers(MetadataReference.CreateFromImage(image), namespaces);
    }

    /// <summary>
    /// Creates an instance of the compiled type and injects services if applicable.
    /// </summary>
    /// <typeparam name="T">The target type</typeparam>
    /// <param name="compiledType">The compiled type to instantiate</param>
    /// <param name="services">Optional services to inject</param>
    /// <returns>The created instance with services injected</returns>
    private static T CreateAndInjectServices<T>(Type compiledType, IScriptServices? services)
    {
        var instance = (T)Activator.CreateInstance(compiledType)!;
        
        // Inject services if the instance is a ScriptBase and services are provided
        if (instance is ScriptBase scriptBase && services != null)
        {
            scriptBase.SetServices(services);
        }
        
        return instance;
    }

    /// <summary>
    /// Compiles the code and loads it into the target context, returning the type to cache.
    ///
    /// Runs under <see cref="CancellationToken.None"/> deliberately: the result is shared by every
    /// caller waiting on the same <see cref="Lazy{T}"/>, so one caller disconnecting must not fail
    /// the compile the others are waiting on.
    /// </summary>
    private CompiledScript CompileAndLoad<T>(
        string code,
        string cacheKey,
        IEnumerable<MetadataReference>? extraReferences,
        IEnumerable<string>? usingDirectives,
        IReadOnlyList<string>? sandboxGrant,
        AssemblyLoadContext? loadContext)
    {
        Interlocked.Increment(ref _compileInvocationCount);

        var syntaxTree = CSharpSyntaxTree.ParseText(code, options: ParseOptions);

        // Add using directives if provided
        if (usingDirectives != null && usingDirectives.Any())
        {
            var root = syntaxTree.GetRoot();
            var usings = usingDirectives.Select(u => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(u)));
            var newRoot = ((CompilationUnitSyntax)root).WithUsings(SyntaxFactory.List(usings));
            syntaxTree = syntaxTree.WithRootAndOptions(newRoot, syntaxTree.Options);
        }

        // The WHOLE cache key, not a prefix: a later change reuses an already-loaded assembly by
        // simple name, which is only exact if the name identifies the compilation uniquely.
        var assemblyName = $"Script_{cacheKey}";

        var compilation = CreateCompilation(assemblyName, [syntaxTree], extraReferences, sandboxGrant);

        // Layer 2 of the sandbox: semantic ban list, run before emit.
        RunSandboxAnalyzer(compilation);

        var image = EmitToImage(compilation, CancellationToken.None);

        // Use the shared collectible context when supplied (so mappings resolve helper types),
        // otherwise a fresh per-script collectible context (so we CAN unload it below when no
        // implementing type is found).
        var context = loadContext ?? new ScriptAssemblyLoadContext(assemblyName);

        // Reuse over reload — but only for a shared context. An assembly already loaded here under
        // this name IS this compilation (the name is the full hash of the compilation inputs), and a
        // shared context cannot unload a single assembly, so if an earlier attempt loaded it and then
        // failed before caching the type, reloading would throw for the rest of the process lifetime.
        // A `loadContext is null` context was just created a few lines above, so the scan can only
        // ever return null there — skip it. `context.Assemblies` accumulates every script assembly
        // ever loaded for the process lifetime (see LoadContextScopes above), so scanning it on every
        // compile into a context that provably cannot contain a match is an unbounded, pointless cost.
        Assembly assembly;
        if (loadContext is null)
        {
            assembly = context.LoadFromStream(new MemoryStream(image));
        }
        else
        {
            var loaded = FindLoadedAssembly(context, assemblyName);
            if (loaded is not null)
            {
                assembly = loaded;
            }
            else
            {
                try
                {
                    assembly = context.LoadFromStream(new MemoryStream(image));
                }
                catch (FileLoadException)
                {
                    // Close the scan/load race across evaluator instances. If another caller loaded
                    // this exact full-cache-key assembly after our scan, that assembly is the desired
                    // result and can be reused safely. If no exact match exists, this is an unrelated
                    // loader failure; preserve it for the normal permanent-failure path.
                    loaded = FindLoadedAssembly(context, assemblyName);
                    if (loaded is null)
                    {
                        throw;
                    }

                    assembly = loaded;
                }
            }
        }

        // Find the type that implements T
        var types = assembly.GetTypes();
        var matchedType = types.FirstOrDefault(t =>
            typeof(T).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        if (matchedType == null)
        {
            if (loadContext is null && context is ScriptAssemblyLoadContext owned)
            {
                owned.Unload();
            }

            var available = string.Join(", ", types.Select(t => t.FullName));
            throw new InvalidOperationException(
                $"No type implementing {typeof(T).FullName} found.\nAvailable types: {available}");
        }

        return new CompiledScript(context, matchedType);
    }

    private static Assembly? FindLoadedAssembly(AssemblyLoadContext context, string assemblyName)
    {
        return context.Assemblies.FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds a Roslyn compilation, selecting the sandboxed reference set when the sandbox is enabled
    /// (baseline ∪ grant) or the full AppDomain reference set otherwise. Runtime-owned contract
    /// references in <paramref name="extraReferences"/> are always included.
    /// </summary>
    private CSharpCompilation CreateCompilation(
        string assemblyName,
        IReadOnlyList<SyntaxTree> trees,
        IEnumerable<MetadataReference>? extraReferences,
        IReadOnlyList<string>? sandboxGrant)
    {
        IEnumerable<MetadataReference> references = _sandbox.Enabled
            ? SandboxedReferenceSet.Build(_sandbox, sandboxGrant)
            : DefaultMetadataReferences.Value;

        if (extraReferences != null)
        {
            references = references.Concat(extraReferences);
        }

        return CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: trees,
            references: references.Distinct(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithAllowUnsafe(_sandbox.AllowUnsafe));
    }

    /// <summary>
    /// Runs the banned-API analyzer. The mandatory banned namespaces (plus <c>DllImport</c>/<c>unsafe</c>)
    /// are enforced on EVERY compile — even when the sandbox is disabled — so mapping code can never use
    /// IO/network/reflection/etc. Operator-configured ban additions and the restricted reference set
    /// apply only when the sandbox is enabled. Throws <see cref="ScriptCompilationException"/> on violations.
    /// </summary>
    private void RunSandboxAnalyzer(Compilation compilation)
    {
        var violations = BannedApiAnalyzer.Analyze(
            compilation, _sandbox, includeConfiguredBans: _sandbox.Enabled);

        if (violations.Count > 0)
        {
            throw new ScriptCompilationException(
                "Sandbox violations:\n  - " + string.Join("\n  - ", violations));
        }
    }

    /// <summary>
    /// Emits the compilation to an in-memory image, throwing on compile errors.
    /// </summary>
    private static byte[] EmitToImage(Compilation compilation, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms, cancellationToken: cancellationToken);

        if (!emitResult.Success)
        {
            var errors = string.Join(Environment.NewLine, emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));

            throw new InvalidOperationException($"Compilation failed:\n{errors}");
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a leading <c>using ...;</c> prefix block from the supplied namespaces.
    /// </summary>
    private static string BuildUsingPrefix(IEnumerable<string>? usingDirectives)
    {
        return usingDirectives is null
            ? string.Empty
            : string.Concat(usingDirectives.Distinct().Select(u => $"using {u};\n"));
    }

    /// <summary>
    /// Derives the cache-key scope for a load context, assigning a stable id on first use. Returns
    /// null for the (very common) no-shared-context case, so the no-helper compile path's keys are
    /// byte-identical to before this existed.
    ///
    /// Root cause this exists for: the helper-set assembly's <see cref="MetadataReference"/> is built
    /// with <c>MetadataReference.CreateFromImage(...)</c>, whose <c>Display</c> is null, so
    /// it contributes nothing to <see cref="GenerateCacheKey"/>'s reference loop below — without this,
    /// two helper sets exporting the same namespace/type could share one cache entry for identical
    /// mapping source and the wrong compiled type would be served with no exception.
    /// </summary>
    private static string? GetCacheScope(AssemblyLoadContext? loadContext)
    {
        if (loadContext is null)
        {
            return null;
        }

        // The factory can run more than once under concurrent first use of the same context —
        // ConditionalWeakTable.GetValue does not guard against that, only against publishing more than
        // one result — so the incrementing id can skip values. That is fine: only uniqueness matters,
        // not density. Do not "fix" this into TryGetValue + Add; that throws on the same race.
        return LoadContextScopes.GetValue(loadContext, _ => $"alc{Interlocked.Increment(ref _loadContextScopeSequence)}");
    }

    /// <summary>
    /// Generates a stable cache key from the code and configuration.
    /// </summary>
    private string GenerateCacheKey(
        string code,
        Type targetType,
        IEnumerable<MetadataReference>? extraReferences,
        IEnumerable<string>? usingDirectives,
        IReadOnlyList<string>? sandboxGrant = null,
        string? cacheScope = null)
    {
        var sb = new StringBuilder();
        sb.Append(code);
        sb.Append('|');
        sb.Append(targetType.AssemblyQualifiedName);

        // Sandbox state is part of the compilation identity.
        sb.Append("|sbx:").Append(_sandbox.Enabled ? '1' : '0');

        if (!string.IsNullOrEmpty(cacheScope))
        {
            sb.Append("|alc:").Append(cacheScope);
        }

        if (sandboxGrant != null)
        {
            foreach (var grant in sandboxGrant.OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("|@@").Append(grant);
            }
        }

        if (usingDirectives != null)
        {
            foreach (var directive in usingDirectives.OrderBy(u => u))
            {
                sb.Append('|');
                sb.Append(directive);
            }
        }

        if (extraReferences != null)
        {
            foreach (var reference in extraReferences.OrderBy(r => r.Display))
            {
                sb.Append('|');
                sb.Append(reference.Display);
            }
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Creates the default metadata references from loaded assemblies.
    /// This is cached and reused across all compilations.
    /// </summary>
    private static IReadOnlyList<MetadataReference> CreateDefaultReferences()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a =>
            {
                try
                {
                    return MetadataReference.CreateFromFile(a.Location);
                }
                catch
                {
                    return null;
                }
            })
            .Where(r => r != null)
            .Cast<MetadataReference>()
            .ToList();
    }

    /// <summary>A compiled script type and the context its assembly was loaded into.</summary>
    private readonly record struct CompiledScript(AssemblyLoadContext Context, Type CompiledType);
}
