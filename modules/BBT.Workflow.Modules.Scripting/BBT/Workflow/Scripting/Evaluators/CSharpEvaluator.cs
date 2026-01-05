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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BBT.Workflow.Scripting.Evaluators;

/// <summary>
/// Memory-safe C# script evaluator with assembly sharing.
/// Caches compiled Types so the same script code reuses the same assembly.
/// Uses collectible AssemblyLoadContext for proper memory management.
/// </summary>
public class CSharpEvaluator : IEvaluator
{
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
    /// Gets the number of cached script types (unique scripts compiled).
    /// </summary>
    public int CachedTypeCount => _typeCache.Count;

    /// <inheritdoc />
    public Task<T> CompileToInstanceAsync<T>(
        string code,
        IScriptServices? services = null,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Code cannot be null or empty", nameof(code));

        // Generate cache key from code + target type + configuration
        var cacheKey = GenerateCacheKey(code, typeof(T), extraReferences, usingDirectives);

        // Try to get cached type and create instance from it
        if (_typeCache.TryGetValue(cacheKey, out var cached))
        {
            var instance = CreateAndInjectServices<T>(cached.CompiledType, services);
            return Task.FromResult(instance);
        }

        // Compile, cache the type, and return instance
        return CompileAndCacheAsync<T>(code, cacheKey, services, extraReferences, usingDirectives, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code, cancellationToken: cancellationToken);

        // Add using directives if provided
        if (usingDirectives != null && usingDirectives.Any())
        {
            var root = syntaxTree.GetRoot(cancellationToken);
            var usings = usingDirectives.Select(u => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(u)));
            var newRoot = ((CompilationUnitSyntax)root).WithUsings(SyntaxFactory.List(usings));
            syntaxTree = syntaxTree.WithRootAndOptions(newRoot, syntaxTree.Options);
        }

        // Use cached references and add any extra ones
        var references = DefaultMetadataReferences.Value;
        if (extraReferences != null && extraReferences.Any())
        {
            references = references.Concat(extraReferences).ToList();
        }

        var assemblyName = $"Script_{cacheKey[..Math.Min(16, cacheKey.Length)]}";
        
        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms, cancellationToken: cancellationToken);

        if (!emitResult.Success)
        {
            var errors = string.Join(Environment.NewLine, emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));

            throw new InvalidOperationException($"Compilation failed:\n{errors}");
        }

        ms.Seek(0, SeekOrigin.Begin);

        // Use collectible context so we CAN unload if needed (e.g., ClearCache)
        var loadContext = new ScriptAssemblyLoadContext(assemblyName);
        var assembly = loadContext.LoadFromStream(ms);

        // Find the type that implements T
        var types = assembly.GetTypes();
        var matchedType = types.FirstOrDefault(t =>
            typeof(T).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        if (matchedType == null)
        {
            loadContext.Unload();
            
            var available = string.Join(", ", types.Select(t => t.FullName));
            throw new InvalidOperationException(
                $"No type implementing {typeof(T).FullName} found.\nAvailable types: {available}");
        }

        // Cache the type for future reuse (same script = same assembly = same type)
        _typeCache.TryAdd(cacheKey, (loadContext, matchedType));

        // Create instance and inject services
        return Task.FromResult(CreateAndInjectServices<T>(matchedType, services));
    }

    /// <summary>
    /// Generates a stable cache key from the code and configuration.
    /// </summary>
    private static string GenerateCacheKey(
        string code, 
        Type targetType,
        IEnumerable<MetadataReference>? extraReferences,
        IEnumerable<string>? usingDirectives)
    {
        var sb = new StringBuilder();
        sb.Append(code);
        sb.Append('|');
        sb.Append(targetType.AssemblyQualifiedName);
        
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
