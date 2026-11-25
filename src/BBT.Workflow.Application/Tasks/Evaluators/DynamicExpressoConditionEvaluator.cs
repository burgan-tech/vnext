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
/// Evaluates condition rules (auto-transition, view, schema / contract, long-poll, state-notification <c>rule</c>) stored as
/// plain text Dynamic Expresso expressions, over the condition-kind scope (<see cref="ExpressionSignatures.Condition"/>):
/// <c>data</c>, <c>input</c>, <c>instance</c>, <c>now</c>, <c>user</c> and the legacy <c>context</c>, each bound only when
/// the expression uses it. A rule that fails (compile, evaluation, a <c>user.roles</c> read on a hop with no caller)
/// returns a failure, which every selection site reads as "does not select"; a caller-roles provider failure returns
/// <c>Authorization:110006</c> and leaves <see cref="BBT.Workflow.Scripting.Caller.ICallerAccessor.ResolutionFailed"/>
/// set on the context's accessor, which the sites check to fail closed.
/// </summary>
public sealed class DynamicExpressoConditionEvaluator : IConditionEvaluator
{
    private readonly ILogger<DynamicExpressoConditionEvaluator> logger;
    private readonly IExpressionCompiler compiler;
    private readonly TimeProvider clock;

    /// <summary>Creates the evaluator on the shared process-wide <see cref="ExpressionCompiler"/> and the system clock.</summary>
    public DynamicExpressoConditionEvaluator(ILogger<DynamicExpressoConditionEvaluator> logger)
        : this(logger, ExpressionCompiler.Shared, TimeProvider.System)
    {
    }

    /// <summary>Creates the evaluator on the given compiler and the system clock.</summary>
    public DynamicExpressoConditionEvaluator(ILogger<DynamicExpressoConditionEvaluator> logger, IExpressionCompiler compiler)
        : this(logger, compiler, TimeProvider.System)
    {
    }

    /// <summary>Creates the evaluator on the given compiler and clock (the DI constructor).</summary>
    public DynamicExpressoConditionEvaluator(
        ILogger<DynamicExpressoConditionEvaluator> logger, IExpressionCompiler compiler, TimeProvider clock)
    {
        this.logger = logger;
        this.compiler = compiler;
        this.clock = clock;
    }

    /// <inheritdoc />
    public string EvaluationType => "Condition";

    /// <inheritdoc />
    public async Task<Result<bool>> EvaluateAsync(
        ScriptCode script,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        if (!ConditionScriptLocations.IsDynamicExpresso(script.Location))
        {
            return Result<bool>.Fail(Error.Validation(
                WorkflowErrorCodes.TransitionRuleFailed,
                "Script location is not configured for Dynamic Expresso evaluation."));
        }

        string expression;
        try
        {
            expression = script.DecodedCode.Trim();
        }
        catch (InvalidOperationException ex)
        {
            logger.DynamicExpressoConditionInvalidEncoding(ex.Message);
            return Result<bool>.Fail(Error.Validation(
                WorkflowErrorCodes.TransitionRuleFailed,
                $"Condition script could not be decoded: {ex.Message}"));
        }

        if (string.IsNullOrWhiteSpace(expression))
        {
            return Result<bool>.Fail(Error.Validation(
                WorkflowErrorCodes.TransitionRuleFailed,
                "Dynamic Expresso condition expression is empty."));
        }

        if (expression.Length > ConditionScriptLocations.MaxDynamicExpressoExpressionLength)
        {
            return Result<bool>.Fail(Error.Validation(
                WorkflowErrorCodes.TransitionRuleFailed,
                $"Dynamic Expresso condition exceeds maximum length ({ConditionScriptLocations.MaxDynamicExpressoExpressionLength})."));
        }

        try
        {
            var compiled = compiler.Compile<ConditionDelegate>(expression, ExpressionSignatures.Condition);
            if (!compiled.IsSuccess)
                throw new InvalidOperationException(compiled.Error.Message);

            // Lazy: only the variables the expression names are materialized; a context-only rule builds the legacy
            // rule context and nothing else, and never resolves the caller's roles.
            var used = compiled.Value!.UsedIdentifiers;
            var scope = ExpressionScope.Create(context, clock, context.Caller, ScopeKind.Condition);
            var prepared = await scope.PrepareAsync(used);
            if (!prepared.IsSuccess)
            {
                // Authorization:110006 — the accessor keeps ResolutionFailed set, so the site fails closed.
                logger.DynamicExpressoConditionEvaluationFailed(prepared.Error.Message ?? prepared.Error.Code);
                return Result<bool>.Fail(prepared.Error);
            }

            var a = scope.Bind(ExpressionSignatures.Condition.Parameters, used);
            var value = compiled.Value.Invoke(
                (RuleJsonDynamic?)a[0], (RuleJsonDynamic?)a[1], (ExpressionInstance?)a[2], (DateTimeOffset)a[3]!,
                (ExpressionUser?)a[4], (ExpressoRuleContext?)a[5]);
            return Result<bool>.Ok(value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var message = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
            logger.DynamicExpressoConditionEvaluationFailed(message);
            return Result<bool>.Fail(Error.Failure(
                WorkflowErrorCodes.TransitionRuleFailed,
                $"Dynamic Expresso evaluation failed: {message}"));
        }
    }
}
