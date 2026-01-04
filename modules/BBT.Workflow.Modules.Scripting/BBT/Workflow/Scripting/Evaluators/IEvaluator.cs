using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Functions;
using Microsoft.CodeAnalysis;

namespace BBT.Workflow.Scripting.Evaluators;

/// <summary>
/// Provides script compilation and instantiation capabilities.
/// Implementations should cache compiled types for performance.
/// </summary>
public interface IEvaluator
{
    /// <summary>
    /// Compiles C# code into an instance of the specified type asynchronously.
    /// If the instance inherits from ScriptBase, the provided services will be injected.
    /// </summary>
    /// <typeparam name="T">The target type to compile the code into</typeparam>
    /// <param name="code">The C# code to compile</param>
    /// <param name="services">Optional script services to inject into ScriptBase instances</param>
    /// <param name="extraReferences">Optional additional metadata references for compilation</param>
    /// <param name="usingDirectives">Optional additional using directives to include</param>
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>A task containing the compiled instance of type T</returns>
    Task<T> CompileToInstanceAsync<T>(
        string code,
        IScriptServices? services = null,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default);
}
