using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that executes resource lock operations (Acquire / Release / Extend)
/// based on the transition's <see cref="ResourceLockDefinition"/>.
/// The lock key is resolved by compiling <c>keyExpression</c> as <see cref="ITransitionMapping"/>
/// and calling its <c>Handler</c> method.
/// </summary>
public sealed class ResourceLockStep(
    IScriptEngine scriptEngine,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IResourceLockService resourceLockService,
    IRuntimeInfoProvider runtimeInfoProvider) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.ResourceLock;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(ResourceLockStep)}");

        var lockDef = context.Transition?.ResourceLock;
        if (lockDef is null)
            return Result<StepOutcome>.Ok(StepOutcome.Continue());

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
        try
        {
            var mapping = await scriptEngine.CompileToInstanceAsync<ITransitionMapping>(
                lockDef.KeyExpression,
                flowScripts: context.Workflow.Scripts,
                cancellationToken: cancellationToken);

            var scriptContext = await BuildScriptContextAsync(context, cancellationToken);

            dynamic result = await mapping.Handler(scriptContext);
            var key = result?.ToString();

            if (string.IsNullOrWhiteSpace(key))
                return Result<string>.Fail(
                    ExecutionErrors.ResourceLockKeyEmpty(context.TransitionKey));

            return Result<string>.Ok(key!);
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(
                ExecutionErrors.ResourceLockKeyResolutionFailed(context.TransitionKey, ex.Message));
        }
    }

    private async Task<Result<StepOutcome>> AcquireAsync(
        TransitionExecutionContext context,
        string resourceKey, string owner, ResourceLockDefinition lockDef, CancellationToken ct)
    {
        var acquired = await resourceLockService.AcquireAsync(resourceKey, owner, lockDef.TtlSeconds, ct);
        if (!acquired)
            return Result<StepOutcome>.Fail(ExecutionErrors.ResourceLockConflict(resourceKey));

        // Record the key on the instance so its terminal cleanup releases the lock automatically,
        // independent of which transition path completes/faults the instance. The pipeline's normal
        // persistence flushes this ExtraProperties change; if the transition rolls back before commit
        // (or the instance snapshot is absent), the lock's TTL remains the safety net — the same
        // fallback as an un-run explicit Release.
        context.Instance?.TrackResourceLock(resourceKey);

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
                .WithHeaders(context.Headers.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value,
                    StringComparer.OrdinalIgnoreCase))
                .WithRouteValues(context.RouteValues.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value,
                    StringComparer.OrdinalIgnoreCase))
                .BuildAsync(ct),
            cancellationToken);
    }
}
