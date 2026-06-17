using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// Cached compiled types indexed by code hash.
    /// Stores (LoadContext, Type) tuple so we can track the context for potential cleanup.
    /// </summary>
    private readonly ConcurrentDictionary<string, (AssemblyLoadContext Context, Type CompiledType)> _typeCache = new();

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
    /// Gets the number of cached script types (unique scripts compiled).
    /// </summary>
    public int CachedTypeCount => _typeCache.Count;

    /// <inheritdoc />
    public Task<T> CompileToInstanceAsync<T>(
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

        // Generate cache key from code + target type + configuration (incl. sandbox grant).
        var cacheKey = GenerateCacheKey(code, typeof(T), extraReferences, usingDirectives, sandboxGrant);

        // Try to get cached type and create instance from it
        if (_typeCache.TryGetValue(cacheKey, out var cached))
        {
            var instance = CreateAndInjectServices<T>(cached.CompiledType, services);
            return Task.FromResult(instance);
        }

        // Compile, cache the type, and return instance
        return CompileAndCacheAsync<T>(
            code, cacheKey, services, extraReferences, usingDirectives, sandboxGrant, loadContext, cancellationToken);
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
    /// Compiles the code, caches the Type, and returns an instance.
    /// </summary>
    private Task<T> CompileAndCacheAsync<T>(
        string code,
        string cacheKey,
        IScriptServices? services,
        IEnumerable<MetadataReference>? extraReferences,
        IEnumerable<string>? usingDirectives,
        IReadOnlyList<string>? sandboxGrant,
        AssemblyLoadContext? loadContext,
        CancellationToken cancellationToken)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code, options: ParseOptions, cancellationToken: cancellationToken);

        // Add using directives if provided
        if (usingDirectives != null && usingDirectives.Any())
        {
            var root = syntaxTree.GetRoot(cancellationToken);
            var usings = usingDirectives.Select(u => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(u)));
            var newRoot = ((CompilationUnitSyntax)root).WithUsings(SyntaxFactory.List(usings));
            syntaxTree = syntaxTree.WithRootAndOptions(newRoot, syntaxTree.Options);
        }

        var assemblyName = $"Script_{cacheKey[..Math.Min(16, cacheKey.Length)]}";

        var compilation = CreateCompilation(assemblyName, [syntaxTree], extraReferences, sandboxGrant);

        // Layer 2 of the sandbox: semantic ban list, run before emit.
        RunSandboxAnalyzer(compilation);

        var image = EmitToImage(compilation, cancellationToken);

        // Use the shared collectible context when supplied (so mappings resolve helper types),
        // otherwise a fresh per-script collectible context (so we CAN unload, e.g. ClearCache).
        var context = loadContext ?? new ScriptAssemblyLoadContext(assemblyName);
        var assembly = context.LoadFromStream(new MemoryStream(image));

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

        // Cache the type for future reuse (same script = same assembly = same type)
        _typeCache.TryAdd(cacheKey, (context, matchedType));

        // Create instance and inject services
        return Task.FromResult(CreateAndInjectServices<T>(matchedType, services));
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
    /// Generates a stable cache key from the code and configuration.
    /// </summary>
    private string GenerateCacheKey(
        string code,
        Type targetType,
        IEnumerable<MetadataReference>? extraReferences,
        IEnumerable<string>? usingDirectives,
        IReadOnlyList<string>? sandboxGrant = null)
    {
        var sb = new StringBuilder();
        sb.Append(code);
        sb.Append('|');
        sb.Append(targetType.AssemblyQualifiedName);

        // Sandbox state is part of the compilation identity.
        sb.Append("|sbx:").Append(_sandbox.Enabled ? '1' : '0');

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

    /// <summary>
    /// Clears all cached types and unloads their assemblies.
    /// Call this to reclaim memory if script definitions change.
    /// </summary>
    public void ClearCache()
    {
        foreach (var key in _typeCache.Keys.ToList())
        {
            if (_typeCache.TryRemove(key, out var cached))
            {
                try
                {
                    cached.Context.Unload();
                }
                catch
                {
                    // Ignore unload failures
                }
            }
        }
    }

    /// <summary>
    /// Removes a specific script from the cache by its code.
    /// Useful when a script definition is updated.
    /// </summary>
    public bool InvalidateScript<T>(
        string code,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null)
    {
        var cacheKey = GenerateCacheKey(code, typeof(T), extraReferences, usingDirectives);
        
        if (_typeCache.TryRemove(cacheKey, out var cached))
        {
            try
            {
                cached.Context.Unload();
            }
            catch
            {
                // Ignore
            }
            return true;
        }
        
        return false;
    }
}
