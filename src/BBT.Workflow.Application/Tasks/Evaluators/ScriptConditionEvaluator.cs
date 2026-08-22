using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Evaluators;

/// <summary>
/// Evaluates condition scripts using the script engine.
/// This is a lightweight evaluator that doesn't go through the full task handler chain.
/// Implements the unified IConditionEvaluator interface.
/// </summary>
public sealed class ScriptConditionEvaluator : IConditionEvaluator
{
    private readonly IScriptEngine _scriptEngine;
    private readonly ILogger<ScriptConditionEvaluator> _logger;
    private readonly IWorkflowMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of ScriptConditionEvaluator.
    /// </summary>
    public ScriptConditionEvaluator(
        IScriptEngine scriptEngine,
        ILogger<ScriptConditionEvaluator> logger,
        IWorkflowMetrics metrics)
    {
        _scriptEngine = scriptEngine;
        _logger = logger;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public string EvaluationType => "Condition";

    /// <inheritdoc />
    public async Task<Result<bool>> EvaluateAsync(
        ScriptCode script,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        return await ResultExtensions.TryAsync(async ct =>
            {
                var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IConditionMapping>(
                    script,
                    flowScripts: context.Workflow?.Scripts,
                    cancellationToken: ct);

                var executeStart = Stopwatch.GetTimestamp();
                try
                {
                    var result = await scriptRunner.Handler(context);
                    _metrics.RecordScriptExecutionDuration(
                        "condition", "csharp", "success",
                        Stopwatch.GetElapsedTime(executeStart).TotalSeconds);
                    return result;
                }
                catch (Exception ex)
                {
                    _metrics.RecordScriptRuntimeError("condition", "csharp", ex.GetType().Name);
                    throw;
                }
            }, cancellationToken)
            .OnFailure(error => _logger.LogError(
                "Condition script evaluation failed: {Error}",
                error.Message));
    }
}