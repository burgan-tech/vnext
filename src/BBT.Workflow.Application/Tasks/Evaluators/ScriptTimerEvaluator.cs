using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Evaluators;

/// <summary>
/// Evaluates timer scripts: transition <c>timer</c> (through <c>TaskCoordinator.ExecuteTimerAsync</c>), workflow
/// <c>attributes.timeout.mapping</c> and the SubFlow timeout override (both through
/// <c>InstanceCommandAppService.ResolveTimeoutScheduleAsync</c>). A script labelled <c>location: "dynamicExpresso"</c>
/// is a Dynamic Expresso expression (<see cref="ValueSlotExpressionEvaluator"/>) whose value becomes the
/// <see cref="TimerSchedule"/>; when it does not evaluate as one it runs as the C# <see cref="ITimerMapping"/> it was
/// before the label was honoured, with a warning. Any other location compiles <see cref="ITimerMapping"/>.
/// This is a lightweight evaluator that doesn't go through the full task handler chain.
/// </summary>
public sealed class ScriptTimerEvaluator : ITimerEvaluator
{
    private readonly IScriptEngine _scriptEngine;
    private readonly ILogger<ScriptTimerEvaluator> _logger;
    private readonly ValueSlotExpressionEvaluator _expressions;

    /// <summary>
    /// Initializes a new instance of ScriptTimerEvaluator.
    /// </summary>
    public ScriptTimerEvaluator(
        IScriptEngine scriptEngine,
        ILogger<ScriptTimerEvaluator> logger,
        ValueSlotExpressionEvaluator? expressions = null)
    {
        _scriptEngine = scriptEngine;
        _logger = logger;
        _expressions = expressions ?? ValueSlotExpressionEvaluator.Shared;
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

        var evaluation = ConditionScriptLocations.IsDynamicExpresso(script.Location)
            ? await ValueSlotExpressionEvaluator.WithScriptFallbackAsync(
                () => _expressions.EvaluateTimerAsync(script, context),
                () => RunScriptAsync(script, context, cancellationToken),
                "timer",
                _logger,
                cancellationToken)
            : await ResultExtensions.TryAsync(ct => RunScriptAsync(script, context, ct), cancellationToken);

        if (!evaluation.IsSuccess)
        {
            _logger.LogError("Timer script evaluation failed: {Error}", evaluation.Error.Message);
            activity.SetResultError(evaluation.Error.Code, evaluation.Error.Message);
        }

        return evaluation;
    }

    private async Task<TimerSchedule> RunScriptAsync(ScriptCode script, ScriptContext context, CancellationToken ct)
    {
        var scriptRunner = await _scriptEngine.CompileToInstanceAsync<ITimerMapping>(
            script,
            flowScripts: context.Workflow?.Scripts,
            cancellationToken: ct);

        // ITimerMapping.Handler directly returns TimerSchedule
        return await scriptRunner.Handler(context);
    }
}
