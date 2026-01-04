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
}
