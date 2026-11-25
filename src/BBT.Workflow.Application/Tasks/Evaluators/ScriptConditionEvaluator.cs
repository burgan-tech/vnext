using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
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

    /// <summary>
    /// Initializes a new instance of ScriptConditionEvaluator.
    /// </summary>
    public ScriptConditionEvaluator(
        IScriptEngine scriptEngine,
        ILogger<ScriptConditionEvaluator> logger)
    {
        _scriptEngine = scriptEngine;
        _logger = logger;
    }

    /// <inheritdoc />
    public string EvaluationType => "Condition";

    /// <inheritdoc />
    public async Task<Result<bool>> EvaluateAsync(
        ScriptCode script,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        // Only Script.Compile used to appear: on a warm cache an auto-transition condition or timer
        // script ran with no span, so its cost was indistinguishable from the step around it.
        using var activity = ScriptActivityHelper.StartExecuteActivity("condition");

        var evaluation = await ResultExtensions.TryAsync(async ct =>
            {
                try
                {
                    var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IConditionMapping>(
                        script,
                        flowScripts: context.Workflow?.Scripts,
                        cancellationToken: ct);

                    return await scriptRunner.Handler(context);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // With its stack trace (vnext#1268): a failing automatic rule records no incident,
                    // so this line is the only place the trace goes. TryAsync still maps the error.
                    _logger.ConditionScriptFailed(context.Instance?.Id ?? Guid.Empty, context.Transition?.Key, ex);
                    throw;
                }
            }, cancellationToken);

        if (!evaluation.IsSuccess)
            activity.SetResultError(evaluation.Error.Code, evaluation.Error.Message);

        return evaluation;
    }
}