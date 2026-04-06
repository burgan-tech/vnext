using System.Collections.Concurrent;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluation;
using DynamicExpresso;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Evaluators;

/// <summary>
/// Evaluates auto-transition (and other) condition rules stored as plain text Dynamic Expresso expressions.
/// </summary>
public sealed class DynamicExpressoConditionEvaluator(ILogger<DynamicExpressoConditionEvaluator> logger) : IConditionEvaluator
{
    private static readonly ConcurrentDictionary<string, Func<ExpressoRuleContext, bool>> CompiledExpressions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string EvaluationType => "Condition";

    /// <inheritdoc />
    public Task<Result<bool>> EvaluateAsync(
        ScriptCode script,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        if (!ConditionScriptLocations.IsDynamicExpresso(script.Location))
        {
            return Task.FromResult(Result<bool>.Fail(Error.Validation(
                WorkflowErrorCodes.TransitionRuleFailed,
                "Script location is not configured for Dynamic Expresso evaluation.")));
        }

        string expression;
        try
        {
            expression = script.DecodedCode.Trim();
        }
        catch (InvalidOperationException ex)
        {
            logger.DynamicExpressoConditionInvalidEncoding(ex.Message);
            return Task.FromResult(Result<bool>.Fail(Error.Validation(
                WorkflowErrorCodes.TransitionRuleFailed,
                $"Condition script could not be decoded: {ex.Message}")));
        }

        if (string.IsNullOrWhiteSpace(expression))
        {
            return Task.FromResult(Result<bool>.Fail(Error.Validation(
                WorkflowErrorCodes.TransitionRuleFailed,
                "Dynamic Expresso condition expression is empty.")));
        }

        if (expression.Length > ConditionScriptLocations.MaxDynamicExpressoExpressionLength)
        {
            return Task.FromResult(Result<bool>.Fail(Error.Validation(
                WorkflowErrorCodes.TransitionRuleFailed,
                $"Dynamic Expresso condition exceeds maximum length ({ConditionScriptLocations.MaxDynamicExpressoExpressionLength}).")));
        }

        try
        {
            var ruleContext = ExpressoRuleContextMapper.FromScriptContext(context);
            var fn = CompiledExpressions.GetOrAdd(expression, CompileExpression);
            var value = fn(ruleContext);
            return Task.FromResult(Result<bool>.Ok(value));
        }
        catch (Exception ex)
        {
            logger.DynamicExpressoConditionEvaluationFailed(ex.Message);
            return Task.FromResult(Result<bool>.Fail(Error.Failure(
                WorkflowErrorCodes.TransitionRuleFailed,
                $"Dynamic Expresso evaluation failed: {ex.Message}")));
        }
    }

    private static Func<ExpressoRuleContext, bool> CompileExpression(string expression)
    {
        var interpreter = new Interpreter(InterpreterOptions.Default);
        var lambda = interpreter.Parse(expression, typeof(bool), new Parameter("context", typeof(ExpressoRuleContext)));
        return lambda.Compile<Func<ExpressoRuleContext, bool>>();
    }
}
