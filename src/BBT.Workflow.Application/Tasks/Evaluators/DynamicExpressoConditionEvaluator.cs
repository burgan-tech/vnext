using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Evaluators;

/// <summary>
/// Evaluates auto-transition (and other) condition rules stored as plain text Dynamic Expresso expressions.
/// </summary>
public sealed class DynamicExpressoConditionEvaluator : IConditionEvaluator
{
    private readonly ILogger<DynamicExpressoConditionEvaluator> logger;
    private readonly IExpressionCompiler compiler;

    /// <summary>Creates the evaluator on the shared process-wide <see cref="ExpressionCompiler"/>.</summary>
    public DynamicExpressoConditionEvaluator(ILogger<DynamicExpressoConditionEvaluator> logger)
        : this(logger, ExpressionCompiler.Shared)
    {
    }

    /// <summary>Creates the evaluator on the given compiler (the DI constructor).</summary>
    public DynamicExpressoConditionEvaluator(ILogger<DynamicExpressoConditionEvaluator> logger, IExpressionCompiler compiler)
    {
        this.logger = logger;
        this.compiler = compiler;
    }

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
            var compiled = compiler.Compile<Func<ExpressoRuleContext, bool>>(expression, ExpressionSignatures.LegacyCondition);
            if (!compiled.IsSuccess)
                throw new InvalidOperationException(compiled.Error.Message);
            var value = compiled.Value.Invoke(ruleContext);
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
}
