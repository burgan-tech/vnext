using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluation;

namespace BBT.Workflow.Tasks.Evaluators;

/// <summary>
/// Routes condition evaluation to Roslyn <see cref="IConditionMapping"/> scripts or Dynamic Expresso based on <see cref="ScriptCode.Location"/>.
/// </summary>
public sealed class RoutingConditionEvaluator(
    ScriptConditionEvaluator roslynEvaluator,
    DynamicExpressoConditionEvaluator expressoEvaluator) : IConditionEvaluator
{
    /// <inheritdoc />
    public string EvaluationType => "Condition";

    /// <inheritdoc />
    public Task<Result<bool>> EvaluateAsync(
        ScriptCode script,
        ScriptContext context,
        CancellationToken cancellationToken = default) =>
        ConditionScriptLocations.IsDynamicExpresso(script.Location)
            ? expressoEvaluator.EvaluateAsync(script, context, cancellationToken)
            : roslynEvaluator.EvaluateAsync(script, context, cancellationToken);
}
