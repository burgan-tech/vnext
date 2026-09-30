using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Evaluators;

/// <summary>
/// Evaluates timer scripts using the script engine.
/// This is a lightweight evaluator that doesn't go through the full task handler chain.
/// Implements the unified ITimerEvaluator interface.
/// </summary>
public sealed class ScriptTimerEvaluator : ITimerEvaluator
{
    private readonly IScriptEngine _scriptEngine;
    private readonly ILogger<ScriptTimerEvaluator> _logger;

    /// <summary>
    /// Initializes a new instance of ScriptTimerEvaluator.
    /// </summary>
    public ScriptTimerEvaluator(
        IScriptEngine scriptEngine,
        ILogger<ScriptTimerEvaluator> logger)
    {
        _scriptEngine = scriptEngine;
        _logger = logger;
    }

    /// <inheritdoc />
    public string EvaluationType => "Timer";

    /// <inheritdoc />
    public async Task<Result<TimerSchedule>> EvaluateAsync(
        ScriptCode script,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        // Only Script.Compile used to appear: on a warm cache an auto-transition condition or timer
        // script ran with no span, so its cost was indistinguishable from the step around it.
        using var activity = ScriptActivityHelper.StartExecuteActivity("timer");

        var evaluation = await ResultExtensions.TryAsync(async ct =>
            {
                var scriptRunner = await _scriptEngine.CompileToInstanceAsync<ITimerMapping>(
                    script,
                    flowScripts: context.Workflow?.Scripts,
                    cancellationToken: ct);
            
                // ITimerMapping.Handler directly returns TimerSchedule
                return await scriptRunner.Handler(context);
            }, cancellationToken)
            .OnFailure(error => _logger.LogError(
                "Timer script evaluation failed: {Error}",
                error.Message));

        if (!evaluation.IsSuccess)
            activity.SetResultError(evaluation.Error.Code, evaluation.Error.Message);

        return evaluation;
    }
}