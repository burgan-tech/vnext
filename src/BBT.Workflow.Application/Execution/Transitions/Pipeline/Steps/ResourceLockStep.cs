using BBT.Aether.Clock;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluators;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that executes resource lock operations (Acquire / Release / Extend)
/// based on the transition's <see cref="ResourceLockDefinition"/>.
/// The lock key is resolved from <c>keyExpression</c>: with <c>location: "dynamicExpresso"</c> it is a Dynamic Expresso
/// expression over the condition-kind scope (<c>data</c>, <c>input</c>, <c>instance</c>, <c>now</c>, <c>user</c>,
/// <c>context</c>) whose string result is the key (<see cref="IDynamicExpressoValueEvaluator"/>, the
/// cache-key evaluator); any other location compiles it as <see cref="ITransitionMapping"/> and calls its <c>Handler</c>.
/// </summary>
/// <remarks>
/// Before the label was honoured, a <c>dynamicExpresso</c>-labelled key ran as a C# script (the script engine ignores
/// the location). Such a key keeps working: when it does not evaluate as an expression it runs as a script, with the
/// warning <c>ResourceLockKeyExpressionRanAsScript</c>; when neither works the expression's error is reported.
/// </remarks>
public sealed class ResourceLockStep(
    IScriptEngine scriptEngine,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IResourceLockService resourceLockService,
    IRuntimeInfoProvider runtimeInfoProvider,
    IClock? clock = null,
    IDynamicExpressoValueEvaluator? expressionEvaluator = null,
    ILogger<ResourceLockStep>? logger = null) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.ResourceLock;

    /// <inheritdoc />
    public async Task<Result<StepOutcome>> ExecuteAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var lockDef = context.Transition?.ResourceLock;
        if (lockDef is null)
            return Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork());

        var keyResult = await ResolveKeyAsync(context, lockDef, cancellationToken);
        if (!keyResult.IsSuccess)
            return Result<StepOutcome>.Fail(keyResult.Error);

        var resourceKey = keyResult.Value!;
        var owner = context.InstanceId.ToString();

        return lockDef.Action switch
        {
            ResourceLockAction.Acquire => await AcquireAsync(context, resourceKey, owner, lockDef, cancellationToken),
            ResourceLockAction.Release => await ReleaseAsync(resourceKey, owner, cancellationToken),
            ResourceLockAction.Extend  => await ExtendAsync(resourceKey, owner, lockDef, cancellationToken),
            _ => Result<StepOutcome>.Fail(
                     ExecutionErrors.ResourceLockInvalidAction(lockDef.Action.ToString()))
        };
    }

    private async Task<Result<string>> ResolveKeyAsync(
        TransitionExecutionContext context,
        ResourceLockDefinition lockDef,
        CancellationToken cancellationToken)
    {
        string? expressionError = null;
        try
        {
            using var scriptActivity = ScriptActivityHelper.StartExecuteActivity("lockKey");

            var scriptContext = await BuildScriptContextAsync(context, cancellationToken);

            if (expressionEvaluator is not null && ConditionScriptLocations.IsDynamicExpresso(lockDef.KeyExpression.Location))
            {
                var evaluated = await expressionEvaluator.EvaluateAsync(lockDef.KeyExpression, scriptContext, cancellationToken);
                if (evaluated.IsSuccess)
                {
                    return string.IsNullOrWhiteSpace(evaluated.Value)
                        ? Result<string>.Fail(ExecutionErrors.ResourceLockKeyEmpty(context.TransitionKey))
                        : Result<string>.Ok(evaluated.Value!);
                }

                // A caller-roles provider failure (Authorization:110006, retryable) is returned as is: never retried as C#.
                if (evaluated.Error.Code == WorkflowErrors.CallerRolesUnavailable().Code)
                    return Result<string>.Fail(evaluated.Error);

                expressionError = evaluated.Error.Message ?? evaluated.Error.Code;
            }

            var mapping = await scriptEngine.CompileToInstanceAsync<ITransitionMapping>(
                lockDef.KeyExpression,
                flowScripts: context.Workflow.Scripts,
                cancellationToken: cancellationToken);

            dynamic result = await mapping.Handler(scriptContext);
            var key = result?.ToString();

            if (expressionError is not null)
                logger?.ResourceLockKeyExpressionRanAsScript(context.TransitionKey, expressionError);

            if (string.IsNullOrWhiteSpace(key))
                return Result<string>.Fail(
                    ExecutionErrors.ResourceLockKeyEmpty(context.TransitionKey));

            return Result<string>.Ok(key!);
        }
        catch (Exception ex)
        {
            // A dynamicExpresso-labelled key that fails as an expression AND as a script reports the expression's
            // error: that is what its author wrote.
            return Result<string>.Fail(
                ExecutionErrors.ResourceLockKeyResolutionFailed(context.TransitionKey, expressionError ?? ex.Message));
        }
    }

    private async Task<Result<StepOutcome>> AcquireAsync(
        TransitionExecutionContext context,
        string resourceKey, string owner, ResourceLockDefinition lockDef, CancellationToken ct)
    {
        var acquired = await resourceLockService.AcquireAsync(resourceKey, owner, lockDef.TtlSeconds, ct);
        if (!acquired)
        {
            // The policy decides what losing the race MEANS, and the two outcomes differ in kind, not
            // degree: Abort treats it as a failure (the pipeline faults the instance), Reject treats it
            // as a normal "not your turn" — the pipeline returns the failure without faulting and hands
            // the Busy reservation back itself (TransitionPipeline, guarded by context.OwnsStatus).
            // Nothing downstream compensates that flip: this path runs no settle, so without that
            // explicit handback the caller would get its 409 and the instance would stay wedged in Busy.
            // Separate error codes keep the Abort/Reject difference visible to the caller instead of
            // hiding it behind one 409.
            return Result<StepOutcome>.Fail(lockDef.OnConflict == ResourceLockConflictPolicy.Reject
                ? ExecutionErrors.ResourceLockRejected(resourceKey)
                : ExecutionErrors.ResourceLockConflict(resourceKey));
        }

        // Record the key on the instance so its terminal cleanup releases the lock automatically,
        // independent of which transition path completes/faults the instance. The pipeline's normal
        // persistence flushes this ExtraProperties change; if the transition rolls back before commit
        // (or the instance snapshot is absent), the lock's TTL remains the safety net — the same
        // fallback as an un-run explicit Release.
        context.Instance?.TrackResourceLock(resourceKey, clock?.UtcNow ?? DateTime.UtcNow);

        return Result<StepOutcome>.Ok(StepOutcome.Continue());
    }

    private async Task<Result<StepOutcome>> ReleaseAsync(
        string resourceKey, string owner, CancellationToken ct)
    {
        // Release is best-effort cleanup and must NEVER fault an otherwise-successful transition:
        // an idempotent no-op (TTL expired / already released) is a success, and a genuine anomaly
        // (lock held by another owner, infra error) is logged/metered by the service but must not
        // roll back the business outcome. The lock's TTL is the ultimate safety net.
        await resourceLockService.ReleaseAsync(resourceKey, owner, ct);
        return Result<StepOutcome>.Ok(StepOutcome.Continue());
    }

    private async Task<Result<StepOutcome>> ExtendAsync(
        string resourceKey, string owner, ResourceLockDefinition lockDef, CancellationToken ct)
    {
        var extended = await resourceLockService.ExtendAsync(resourceKey, owner, lockDef.TtlSeconds, ct);
        if (!extended)
            return Result<StepOutcome>.Fail(ExecutionErrors.ResourceLockConflict(resourceKey));

        return Result<StepOutcome>.Ok(StepOutcome.Continue());
    }

    private async Task<ScriptContext> BuildScriptContextAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        return await context.GetOrBuildScriptContextAsync(
            ct => scriptContextFactory.NewBuilder(instanceRepository)
                .WithRuntime(runtimeInfoProvider)
                .WithWorkflow(context.Workflow)
                .WithInstance(context.Instance)
                .WithTransition(context.Transition)
                .WithBody(context.Data)
                .WithHeaders(context.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
                .BuildAsync(ct),
            cancellationToken);
    }
}
