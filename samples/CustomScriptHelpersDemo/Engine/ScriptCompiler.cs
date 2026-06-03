using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CustomScriptHelpersDemo.Engine;

/// <summary>Thrown when sandboxed compilation fails (Roslyn errors or sandbox violations).</summary>
public sealed class ScriptCompilationException(string message) : Exception(message);

/// <summary>Result of compiling a set of sources into one assembly.</summary>
public sealed record CompiledAssembly(Assembly Assembly, MetadataReference Reference, string[] Namespaces);

/// <summary>
/// Compiles C# source into an assembly under <see cref="SandboxOptions"/>, loading
/// the result into a supplied collectible <see cref="AssemblyLoadContext"/> so that
/// the helper assembly and the mapping that references it resolve against each other.
/// </summary>
public sealed class ScriptCompiler(SandboxOptions sandbox)
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12);

    public CompiledAssembly Compile(
        string assemblyName,
        IReadOnlyList<(string Path, string Code)> sources,
        AssemblyLoadContext loadContext,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null)
    {
        var usings = usingDirectives is null
            ? string.Empty
            : string.Concat(usingDirectives.Select(u => $"using {u};\n"));

        var trees = sources
            .Select(s => CSharpSyntaxTree.ParseText(usings + s.Code, ParseOptions, path: s.Path))
            .ToList();

        var references = SandboxedReferenceSet.Build(sandbox).ToList();
        if (extraReferences is not null)
            references.AddRange(extraReferences);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release));

        // Layer 2 of the sandbox: semantic ban list. Run before emit.
        var violations = BannedApiAnalyzer.Analyze(compilation, sandbox);
        if (violations.Count > 0)
            throw new ScriptCompilationException(
                "Sandbox violations:\n  - " + string.Join("\n  - ", violations));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var errors = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());
            throw new ScriptCompilationException("Compilation failed:\n  - " + string.Join("\n  - ", errors));
        }

        ms.Seek(0, SeekOrigin.Begin);
        var image = ms.ToArray();

        var assembly = loadContext.LoadFromStream(new MemoryStream(image));
        var namespaces = assembly.GetExportedTypes()
            .Select(t => t.Namespace)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .Cast<string>()
            .ToArray();

        return new CompiledAssembly(assembly, MetadataReference.CreateFromImage(image), namespaces);
    }
}
