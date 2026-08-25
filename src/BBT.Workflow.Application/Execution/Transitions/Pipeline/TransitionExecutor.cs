using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Executes the ordered lifecycle steps of EXACTLY ONE transition.
/// <para>
/// This is the single-transition execution primitive: it owns no distributed lock,
/// no auto-chain loop, and no enqueue. It runs the steps for the given context
/// (already validated and profiled) applying <see cref="StepOutcome"/> flow control
/// (Continue / Stop / SkipTo / replan), and mutates the context in place.
/// </para>
/// <para>
/// Chaining, locking and post-commit orchestration remain the responsibility of
/// <see cref="TransitionPipeline"/>. "What happens next" is exposed as data via
/// <see cref="PipelineDirectives.ToContinuations"/> on the context's directives.
/// </para>
/// </summary>
public sealed class TransitionExecutor
{
    private readonly IReadOnlyList<ITransitionStep> _steps;
    private readonly ILogger<TransitionExecutor> _logger;
    /// <summary>
    /// Initializes a new instance of the <see cref="TransitionExecutor"/>.
    /// </summary>
    public TransitionExecutor(
        IEnumerable<ITransitionStep> steps,
        ILogger<TransitionExecutor> logger)
    {
        _steps = steps.OrderBy(s => s.Order).ToList();
        _logger = logger;
    }

    /// <summary>
    /// Executes a single transition's pipeline steps against the supplied context.
    /// The context is mutated in place; continuation work (next transition,
    /// post-commit jobs, resolved status) is accumulated on <c>context.Directives</c>.
    /// </summary>
    /// <param name="context">The validated, profiled transition execution context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Ok on success; Fail with the originating error on an unhandled step failure.</returns>
    [Trace]
    public async Task<Result> ExecuteOneAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        EnrichTelemetry(context);

        var profile = context.Profile ?? PipelineExecutionProfile.ForManual();
        var state = CreateInitialState(context, profile);

        using (_logger.BeginScope(BuildLogScope(context)))
        {
            try
            {
                while (state.HasMoreSteps())
                {
                    if (context.SkipImmediateExecution)
                        return Result.Ok();

                    var stepResult = await ExecuteStepWithBoundaryAsync(
                        state.CurrentStep, context, cancellationToken);

                    if (!stepResult.IsSuccess)
                        return Result.Fail(stepResult.Error);

                    var flowControl = DetermineFlowControl(stepResult.Value!, state.CurrentStep, context, state);

                    if (flowControl.ShouldStop)
                        break;

                    if (flowControl.ShouldReplan)
                    {
                        state = CreateInitialState(context, profile);
                        continue;
                    }

                    state = state.MoveNext();
                }

                return Result.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in pipeline execution for workflow {WorkflowKey}",
                    context.Workflow.Key);
                return Result.Fail(Error.Failure("PipelineException", ex.Message));
            }
        }
    }

    /// <summary>
    /// Builds a log scope dictionary for the current transition.
    /// </summary>
    private static Dictionary<string, object> BuildLogScope(TransitionExecutionContext context)
    {
        var props = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain]    = context.Domain,
            [TelemetryConstants.TagNames.Flow]    = context.Workflow.Key,
            [TelemetryConstants.TagNames.FlowVersion]    = context.Workflow.Version,
            [TelemetryConstants.TagNames.InstanceId]    = context.InstanceId,
            [TelemetryConstants.TagNames.InstanceKey]    = context.Instance.Key ?? "N/A",
            [TelemetryConstants.TagNames.StateFrom]     = context.Transition?.From ?? context.Instance.GetCurrentState,
            [TelemetryConstants.TagNames.StateTo]       = context.Transition?.Target ?? "N/A",
            [TelemetryConstants.TagNames.TransitionKey] = context.TransitionKey,
            [TelemetryConstants.TagNames.TriggerType]    = context.Transition?.TriggerType.ToString() ?? "N/A",
            [TelemetryConstants.TagNames.CorrelationId] = context.CorrelationId,
            ["ChainDepth"] = context.ChainDepth,
            ["PipelineProfile"] = context.Profile?.Name ?? "unknown",
        };
        if (context.Headers.TryGetValue(TelemetryConstants.HeaderNames.ParentInstanceId, out var raw)
            && Guid.TryParse(raw, out var parentId))
        {
            props[TelemetryConstants.TagNames.ParentInstanceId] = parentId;
        }
        // Stamp root instance ID for every subflow pipeline execution (no-op on root instances)
        var rootId = context.Instance.GetRootInstanceId();
        if (rootId != context.InstanceId)
        {
            props[TelemetryConstants.TagNames.RootInstanceId] = rootId;
        }
        return props;
    }

    /// <summary>
    /// Executes a pipeline step with exception boundary.
    /// </summary>
    private async Task<Result<StepOutcome>> ExecuteStepWithBoundaryAsync(
        ITransitionStep step,
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        using var stepActivity = PipelineStepActivityHelper.StartStepActivity(step);
        try
        {
            var result = await step.ExecuteAsync(context, cancellationToken);
            if (result.IsSuccess)
                PipelineStepActivityHelper.SetStepOutcome(stepActivity, result.Value!);
            else
                stepActivity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);

            return result;
        }
        catch (Exception ex)
        {
            stepActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Unhandled exception in step {StepName}", step.Name);
            return Result<StepOutcome>.Fail(Error.Failure(ex.GetType().Name, ex.Message));
        }
    }

    private static void EnrichTelemetry(TransitionExecutionContext context)
    {
        var activity = Activity.Current;
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.Flow, context.Workflow.Key);
        activity.SetTag(TelemetryConstants.TagNames.FlowVersion, context.Workflow.Version);
        activity.SetTag(TelemetryConstants.TagNames.InstanceId, context.InstanceId.ToString());
        activity.SetTag(TelemetryConstants.TagNames.TransitionKey, context.TransitionKey);
        activity.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Orchestration);
        activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        if (context.Transition != null)
        {
            activity.SetTag(TelemetryConstants.TagNames.TriggerType, context.Transition.TriggerType.ToString());
        }

        activity.SetTag(TelemetryConstants.TagNames.ChainDepth, context.ChainDepth);
        activity.SetTag("vnext.pipeline.profile", context.Profile?.Name ?? "unknown");
        activity.SetTag("vnext.chain.id", context.ExecutionChainId);

        // Business correlation + vendor-neutral instance id: published for EVERY pipeline run
        // (sync and async alike) so downstream stampers (RemoteInvokerService.CreateTraceContext,
        // ApplyTrustedCorrelationHeaders) read one consistent source. CorrelationId is the
        // chain-stable execution correlation id, distinct from the per-request X-Request-Id.
        activity.SetTag(TelemetryConstants.TagNames.CorrelationId, context.CorrelationId);
        activity.SetTag(TelemetryConstants.TagNames.WorkflowInstanceId,
            context.InstanceId.ToString("D").ToLowerInvariant());

        activity.SetBaggage(TelemetryConstants.TagNames.Flow, context.Workflow.Key);
        activity.SetBaggage(TelemetryConstants.TagNames.FlowVersion, context.Workflow.Version);
        activity.SetBaggage(TelemetryConstants.TagNames.InstanceId, context.InstanceId.ToString());
        activity.SetBaggage(TelemetryConstants.TagNames.CorrelationId, context.CorrelationId);
        activity.SetBaggage(TelemetryConstants.TagNames.WorkflowInstanceId,
            context.InstanceId.ToString("D").ToLowerInvariant());

        // Stamped unconditionally, not only for subflows: with hops spread across sibling lanes,
        // the root instance id is the single filter that selects a whole business request in APM.
        var rootId = context.Instance.GetRootInstanceId();
        activity.SetTag(TelemetryConstants.TagNames.RootInstanceId, rootId.ToString());
        if (rootId != context.InstanceId)
        {
            activity.SetBaggage(TelemetryConstants.TagNames.RootInstanceId, rootId.ToString());
        }

        activity.SetDisplayName($"transition/{context.TransitionKey}");
    }

    /// <summary>
    /// Creates initial pipeline state with execution plan.
    /// </summary>
    private PipelineState CreateInitialState(TransitionExecutionContext context, PipelineExecutionProfile profile)
    {
        var plan = BuildExecutionPlan(context, profile);
        _logger.PipelineExecutingWithProfile(profile.Name, plan.Count, context.ChainDepth);
        var excludedCount = _steps.Count - plan.Count;
        if (excludedCount > 0)
            _logger.ProfileExcludedSteps(profile.Name, excludedCount, context.TransitionKey);

        return new PipelineState(plan, 0);
    }

    /// <summary>
    /// Builds an execution plan by filtering and ordering steps based on context directives.
    /// </summary>
    private IReadOnlyList<ITransitionStep> BuildExecutionPlan(
        TransitionExecutionContext context,
        PipelineExecutionProfile profile)
    {
        var ordered = _steps
            .Where(s => !profile.ExcludedStepOrders.Contains(s.Order))
            .ToList();

        var startOrder = context.Directives.ConsumeResumeFrom();
        if (startOrder.HasValue)
            ordered = ordered.Where(s => s.Order >= startOrder.Value).ToList();

        if (context.Directives.TerminalReached)
        {
            var maxOrder = LifecycleOrder.Finalize;
            ordered = ordered.Where(s => s.Order <= maxOrder).ToList();
        }

        if (context.Directives.Epilogue == EpilogueMode.Skip)
        {
            ordered = ordered
                .Where(s => s.Order != LifecycleOrder.Schedule &&
                            s.Order != LifecycleOrder.Auto)
                .ToList();
        }

        return ordered;
    }

    /// <summary>
    /// Determines flow control based on step outcome.
    /// </summary>
    private static FlowControl DetermineFlowControl(
        StepOutcome outcome,
        ITransitionStep step,
        TransitionExecutionContext context,
        PipelineState state)
    {
        outcome.MutateDirectives?.Invoke(context.Directives);

        if (outcome.StopPipeline)
            return FlowControl.Stop();

        if (outcome.SkipToOrder is { } skipTo)
        {
            context.Directives.RequestResumeFrom(skipTo);
            return FlowControl.Replan();
        }

        if (NeedsReplan(state.Plan, context.Directives))
        {
            context.Directives.RequestResumeFrom(step.Order + 1);
            return FlowControl.Replan();
        }

        return FlowControl.Continue();
    }

    private static bool NeedsReplan(IReadOnlyList<ITransitionStep> currentPlan, PipelineDirectives d)
    {
        if (d.TerminalReached)
            return true;

        if (d.Epilogue == EpilogueMode.Skip &&
            currentPlan.Any(s => s.Order == LifecycleOrder.Schedule || s.Order == LifecycleOrder.Auto))
            return true;

        if (d.ResumeFromOrder is not null)
            return true;

        return false;
    }

    private readonly record struct PipelineState(IReadOnlyList<ITransitionStep> Plan, int Index)
    {
        public ITransitionStep CurrentStep => Plan[Index];
        public bool HasMoreSteps() => Index < Plan.Count;
        public PipelineState MoveNext() => this with { Index = Index + 1 };
    }

    private readonly record struct FlowControl(bool ShouldStop, bool ShouldReplan)
    {
        public static FlowControl Stop() => new(ShouldStop: true, ShouldReplan: false);
        public static FlowControl Replan() => new(ShouldStop: false, ShouldReplan: true);
        public static FlowControl Continue() => new(ShouldStop: false, ShouldReplan: false);
    }
}
