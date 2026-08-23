using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting.Functions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Represents a script engine that combines script running and compilation capabilities.
/// Provides a unified interface for executing C# scripts and compiling them to instances.
/// </summary>
public interface IScriptEngine : IScriptCompiler
{
    /// <summary>
    /// Compiles (or cache-hits) the mapping once and returns a factory producing a FRESH,
    /// service-injected instance per call. Compile-time behaviour and metrics are identical to
    /// <see cref="IScriptCompiler.CompileToInstanceAsync{T}(ScriptCode, ScriptSettings?, IEnumerable{MetadataReference}?, IEnumerable{string}?, CancellationToken)"/> —
    /// this exists so a caller invoking the same mapping across multiple phases (e.g. input mapping
    /// then output mapping) pays the engine exactly once instead of once per phase.
    /// </summary>
    /// <typeparam name="T">The target type to compile the code into.</typeparam>
    /// <param name="scriptCode">The mapping script (code/encoding, optional <c>scripts</c> settings).</param>
    /// <param name="flowScripts">Optional flow-level (workflow) script settings, unioned with the
    /// mapping-level <c>scripts</c> (helpers concatenated/deduped, allowed assemblies merged).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task containing a factory that produces a fresh, service-injected instance of
    /// type <typeparamref name="T"/> on every call.</returns>
    Task<Func<T>> CompileToFactoryAsync<T>(
        ScriptCode scriptCode,
        ScriptSettings? flowScripts = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides capabilities for compiling C# code into executable instances.
/// Supports compilation with custom metadata references and using directives.
/// </summary>
public interface IScriptCompiler
{
    /// <summary>
    /// Compiles C# code into an instance of the specified type asynchronously.
    /// If the instance inherits from ScriptBase, services will be automatically injected.
    /// </summary>
    /// <typeparam name="T">The target type to compile the code into</typeparam>
    /// <param name="code">The C# code to compile</param>
    /// <param name="extraReferences">Optional additional metadata references for compilation</param>
    /// <param name="usingDirectives">Optional additional using directives to include</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>A task containing the compiled instance of type T</returns>
    /// <exception cref="CompilationErrorException">Thrown when the code contains compilation errors</exception>
    /// <exception cref="InvalidOperationException">Thrown when the code cannot be compiled to the target type</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled</exception>
    Task<T> CompileToInstanceAsync<T>(
        string code,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compiles a mapping <see cref="ScriptCode"/> into an instance of the specified type.
    /// When the script declares helper references (<see cref="ScriptCode.Helpers"/>), the referenced
    /// helper set is built first (sandboxed, cached by content hash), its assembly referenced and its
    /// public namespaces auto-imported, and the mapping is compiled into the helper set's load context.
    /// When no helpers are declared this is equivalent to the string overload.
    /// </summary>
    /// <typeparam name="T">The target type to compile the code into.</typeparam>
    /// <param name="scriptCode">The mapping script (code/encoding, optional <c>scripts</c> settings).</param>
    /// <param name="flowScripts">Optional flow-level (workflow) script settings, unioned with the
    /// mapping-level <c>scripts</c> (helpers concatenated/deduped, allowed assemblies merged).</param>
    /// <param name="extraReferences">Optional additional metadata references for compilation.</param>
    /// <param name="usingDirectives">Optional additional using directives to include.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task containing the compiled instance of type T.</returns>
    Task<T> CompileToInstanceAsync<T>(
        ScriptCode scriptCode,
        ScriptSettings? flowScripts = null,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default);
}
