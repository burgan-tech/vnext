using BBT.Workflow.Monitoring;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Functions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;
using System.Diagnostics;
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
public sealed class ScriptEngine(
    IEvaluator evaluator,
    IScriptServices scriptServices,
    IWorkflowMetrics workflowMetrics) : IScriptEngine
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

    /// <summary>
    /// Lazily-initialized default metadata references used for script compilation.
    /// Includes core .NET types, collections, and workflow-specific assemblies.
    /// </summary>
    private static readonly Lazy<MetadataReference[]> DefaultReferences = new(() =>
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(TimerSchedule).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Dictionary<,>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(ScriptBase).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(JsonSerializableAttribute).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Text.Encodings.Web.JavaScriptEncoder).Assembly.Location),
    ]);

    /// <summary>
    /// Default using directives automatically included in all scripts.
    /// Provides access to common .NET namespaces and workflow-specific types.
    /// </summary>
    private static readonly string[] DefaultUsings =
    {
        "System",
        "System.IO",
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
        "BBT.Workflow.Runtime",
        "BBT.Workflow.Scripting.Functions",
        "BBT.Workflow.Definitions.Timer"
    };
    
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
    /// <param name="cancellationToken">Token to cancel the operation</param>
    /// <returns>A task containing the compiled instance of type T</returns>
    /// <exception cref="CompilationErrorException">Thrown when the code contains compilation errors</exception>
    /// <exception cref="InvalidOperationException">Thrown when the code cannot be compiled to the target type</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled</exception>
    public async Task<T> CompileToInstanceAsync<T>(
        string code,
        IEnumerable<MetadataReference>? extraReferences = null,
        IEnumerable<string>? usingDirectives = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        const string scriptType = "compilation";
        const string language = "csharp";

        try
        {
            // Use cached default references
            var mergedReferences = (extraReferences ?? [])
                .Concat(DefaultReferences.Value)
                .Distinct();

            // Use cached default usings
            var mergedUsings = (usingDirectives ?? [])
                .Concat(DefaultUsings)
                .Distinct();

            // Pass script services to the evaluator for injection into ScriptBase instances
            var result = await _evaluator.CompileToInstanceAsync<T>(
                code, 
                _scriptServices,
                mergedReferences, 
                mergedUsings, 
                cancellationToken);

            stopwatch.Stop();
            var durationSeconds = stopwatch.Elapsed.TotalSeconds;

            // Record successful script compilation
            workflowMetrics.RecordScriptExecution(scriptType, language, "success");
            workflowMetrics.RecordScriptCompilationDuration(scriptType, language, "success", durationSeconds);

            return result;
        }
        catch (CompilationErrorException ex)
        {
            stopwatch.Stop();
            var durationSeconds = stopwatch.Elapsed.TotalSeconds;

            // Record compilation error
            workflowMetrics.RecordScriptExecution(scriptType, language, "compilation_error");
            workflowMetrics.RecordScriptCompilationError(scriptType, language, ex.GetType().Name);
            workflowMetrics.RecordScriptCompilationDuration(scriptType, language, "compilation_error", durationSeconds);

            throw;
        }
        catch (InvalidOperationException ex)
        {
            stopwatch.Stop();
            var durationSeconds = stopwatch.Elapsed.TotalSeconds;

            // Record invalid operation as compilation error
            workflowMetrics.RecordScriptExecution(scriptType, language, "invalid_operation");
            workflowMetrics.RecordScriptCompilationError(scriptType, language, ex.GetType().Name);
            workflowMetrics.RecordScriptCompilationDuration(scriptType, language, "invalid_operation", durationSeconds);

            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var durationSeconds = stopwatch.Elapsed.TotalSeconds;

            // Record cancelled compilation
            workflowMetrics.RecordScriptExecution(scriptType, language, "cancelled");
            workflowMetrics.RecordScriptCompilationDuration(scriptType, language, "cancelled", durationSeconds);

            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var durationSeconds = stopwatch.Elapsed.TotalSeconds;

            // Record unexpected compilation error
            workflowMetrics.RecordScriptExecution(scriptType, language, "unexpected_error");
            workflowMetrics.RecordScriptCompilationError(scriptType, language, ex.GetType().Name);
            workflowMetrics.RecordScriptCompilationDuration(scriptType, language, "unexpected_error", durationSeconds);

            throw;
        }
    }
}
