using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using System.Diagnostics;
using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Transitions.Factory;

/// <inheritdoc />
public sealed class TransitionContextFactory(
    IInstanceRepository instanceRepository,
    IComponentCacheStore componentCacheStore,
    IRuntimeInfoProvider runtimeInfoProvider) : ITransitionContextFactory
{
    /// <inheritdoc />
    /// <summary>
    /// Creates a TransitionExecutionContext from the input.
    /// Railway chain: Validate Domain → Rehydrate Instance → Resolve State/Transition → Build Context
    /// </summary>
    public async Task<Result<TransitionExecutionContext>> CreateAsync(
        WorkflowExecutionContext input,
        CancellationToken cancellationToken)
    {
        using var activity = PipelineStepActivityHelper.StartOperationActivity("Transition.LoadContext");
        var result = await ValidateDomain(input.Domain)
            .BindAsync(_ => RehydrateInstanceAsync(input, cancellationToken))
            .ThenAsync(data => Task.FromResult(ResolveStateAndTransition(data, input)))
            .MapAsync(data => BuildExecutionContext(data, input));

        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);

        return result;
    }

    /// <summary>
    /// Validates the domain using runtime info provider.
    /// Converts potential validation exception to Result.Fail.
    /// </summary>
    private Result<string> ValidateDomain(string domain)
    {
        try
        {
            runtimeInfoProvider.Check(domain);
            return Result<string>.Ok(domain);
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(
                WorkflowErrors.DomainValidationFailed(domain, ex.Message));
        }
    }

    /// <summary>
    /// Rehydrates the workflow and instance from storage.
    /// Combines GetFlowAsync and GetActiveAsync Results using Railway pattern.
    /// </summary>
    private Task<Result<(Definitions.Workflow Workflow, Instance Instance)>> RehydrateInstanceAsync(
        WorkflowExecutionContext input,
        CancellationToken cancellationToken)
    {
        return componentCacheStore.GetFlowAsync(
                input.Domain, input.WorkflowKey, input.WorkflowVersion, cancellationToken)
            .BindAsync(workflow =>
                LoadInstanceAsync(input.InstanceId, cancellationToken)
                    .MapAsync(instance => (workflow, instance)));
    }

    /// <summary>
    /// Loads the active instance by identifier, wrapped in its own span so instance-load
    /// latency is attributable independent of the flow-cache lookup that precedes it.
    /// </summary>
    private async Task<Result<Instance>> LoadInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        using var activity = PipelineStepActivityHelper.StartOperationActivity("Instance.Load");
        var result = await instanceRepository.GetActiveAsync(instanceId, cancellationToken);
        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);

        return result;
    }

    /// <summary>
    /// Resolves the current state and transition using Railway pattern.
    /// Uses Map to transform GetState result - no throwing on domain errors.
    /// </summary>
    private Result<(Definitions.Workflow Workflow, Instance Instance, State CurrentState, Transition? Transition)>
        ResolveStateAndTransition(
            (Definitions.Workflow Workflow, Instance Instance) data,
            WorkflowExecutionContext input)
    {
        return data.Workflow.GetState(data.Instance.GetCurrentState)
            .Map(currentState =>
            {
                // In Resume mode, transition is optional (e.g., SubFlow completion scenarios)
                var transition = input.Mode == ExecMode.Resume
                    ? null
                    : ResolveTransition(data.Workflow, currentState, input.TransitionKey);

                return (data.Workflow, data.Instance, currentState, transition);
            });
    }

    /// <summary>
    /// Builds the final TransitionExecutionContext.
    /// </summary>
    private TransitionExecutionContext BuildExecutionContext(
        (Definitions.Workflow Workflow, Instance Instance, State CurrentState, Transition? Transition) data,
        WorkflowExecutionContext input)
    {
        var (traceId, spanId) = InitializeTelemetry();

        var executionContext = new TransitionExecutionContext
        {
            // Identity
            Domain = input.Domain,
            InstanceId = data.Instance.Id,
            WorkflowKey = data.Workflow.Key,
            TransitionKey = data.Transition?.Key ?? input.TransitionKey,
            Trigger = data.Transition?.TriggerType ?? input.TriggerType,
            Actor = input.Actor,
            CorrelationId = input.CorrelationId ?? Guid.NewGuid().ToString("N"),
            CausationId = input.CausationId,
            ExecutionChainId = input.Execution?.ExecutionChainId ?? Guid.NewGuid().ToString("N"),
            ChainDepth = input.Execution?.ChainDepth ?? 0,
            RequestedAt = input.RequestedAt ?? DateTimeOffset.UtcNow,

            // Definitions
            Workflow = data.Workflow,
            Current = data.CurrentState,
            Transition = data.Transition,

            // Instance state
            Instance = data.Instance,
            Data = input.Data?.Attributes,
            InstanceKey = input.Data?.Key,
            Tags = input.Data?.Tags,
            Stage = input.Data?.Stage,

            // Flags
            Mode = input.Mode,
            CallerMode = input.CallerMode,
            IsReentry = input.IsReentry,
            IsPreReserved = input.IsPreReserved,
            SubflowChainReserved = input.SubflowChainReserved,
            RetryOfTransitionRecordId = input.Retry?.TransitionId,
            IsErrorBoundaryTransition = input.IsErrorBoundaryTransition,
            Termination = input.Termination,

            // Telemetry
            TraceId = traceId,
            SpanId = spanId,
            Headers = ToCaseInsensitiveHeaders(input.Headers)
        };

        // Configure pipeline directives
        if (input.Execution?.ResumeFrom.HasValue == true)
            executionContext.Directives.RequestResumeFrom(input.Execution.ResumeFrom.Value);

        if (input.Execution?.IsSubFlowResume == true)
            executionContext.Directives.MarkAsSubFlowResume(input.Execution.SubFlowResumeInstanceId);

        if (input.Execution?.IsLongPollAckResume == true)
            executionContext.Directives.MarkAsLongPollAckResume();

        if (input.Execution?.IsTimeoutTransition == true)
            executionContext.Directives.MarkAsTimeoutTransition();

        return executionContext;
    }

    /// <summary>
    /// Builds a case-insensitive view of the request headers. HTTP headers are case-insensitive,
    /// but upstream hops may lower-case keys (e.g. <c>x-parent-instance-id</c>) while the runtime
    /// stamps the canonical form (<c>X-Parent-Instance-Id</c>). A case-insensitive dictionary makes
    /// both lookups (e.g. <c>TryGetValue</c>) and writes (replace, not duplicate) behave correctly.
    /// Copy uses last-wins so any case-variant duplicates collapse into a single entry.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> ToCaseInsensitiveHeaders(
        IReadOnlyDictionary<string, string?>? headers)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
            return result;

        foreach (var kv in headers)
            result[kv.Key] = kv.Value;

        return result;
    }

    /// <inheritdoc />
    public Result<TransitionExecutionContext> CreateFromPreloaded(
        WorkflowExecutionContext input,
        Definitions.Workflow workflow,
        Instance instance)
    {
        return ResolveStateAndTransition((workflow, instance), input)
            .Map(data => BuildExecutionContext(data, input));
    }

    /// <summary>
    /// Resolves and validates the transition for the given trigger type.
    /// </summary>
    private static Transition? ResolveTransition(
        Definitions.Workflow workflow,
        State currentState,
        string transitionKey)
    {
        return workflow.ResolveTransition(transitionKey, currentState) ??
               workflow.FindTransitionInContext(transitionKey);
    }

    /// <summary>
    /// Initializes telemetry context for distributed tracing.
    /// </summary>
    private static (string TraceId, string SpanId) InitializeTelemetry()
    {
        var activity = Activity.Current;
        var traceId = activity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var spanId = activity?.SpanId.ToString() ?? Guid.NewGuid().ToString("N")[..16];

        return (traceId, spanId);
    }
}
