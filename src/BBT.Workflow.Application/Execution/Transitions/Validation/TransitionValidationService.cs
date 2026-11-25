using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Policies;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Transitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Shared;
using BBT.Workflow.Validation;
using BBT.Workflow.Versions;
using System.Diagnostics;
using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Validation;

/// <inheritdoc />
public class TransitionValidationService(
    TransitionExecutionPolicy transitionExecutionPolicy,
    IJsonSchemaValidator schemaValidator,
    IComponentCacheStore componentCacheStore,
    IVersionRegistryWriteGuard versionRegistryWriteGuard,
    ITransitionSchemaResolver schemaResolver,
    IInstanceDataWriteService? instanceDataWriteService = null) : ITransitionValidationService
{
    /// <inheritdoc />
    /// <summary>
    /// Validates a transition execution context.
    /// Railway chain: Transition Schema → State Machine Policy → Master Schema Preview
    /// </summary>
    public async Task<Result> ValidateAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        using var activity = PipelineStepActivityHelper.StartTransitionActivity(
            "Transition.Validate", context.TransitionKey);

        // 1. Transition schema over the body as sent
        var result = await ValidateSchemaAsync(context, cancellationToken);

        // 2. State Machine Validation using Specification Pattern
        // Includes: Actor authorization, state transition rules, SubFlow bypass, etc.
        if (result.IsSuccess)
            result = await ValidatePolicyAsync(context, cancellationToken);

        // 3. Master schema over the body merged onto the stored data. After the policy on purpose:
        // it reads the stored document and its cost grows with it, so a request the policy refuses
        // (wrong state, unauthorized actor) never reaches it and never sees a master-schema message.
        if (result.IsSuccess)
            result = await PreviewMasterSchemaAsync(context, cancellationToken);

        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Spanned like <see cref="ValidateAsync"/> even though it does no I/O: it runs on every hop of
    /// an auto-chain, and a trace that shows the schema-bearing validation but not this one reads
    /// as if the later hops validated nothing.
    /// </remarks>
    public Task<Result> ValidatePolicyAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        using var activity = PipelineStepActivityHelper.StartTransitionActivity(
            "Transition.ValidatePolicy", context.TransitionKey);
        var result = transitionExecutionPolicy.Validate(context);
        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Master-schema preview (vnext#1255): the write funnel validates the merged document inside the
    /// pipeline, where a failure faults the instance. A caller's body is checked here first, before
    /// admission, so one field a closed master schema does not allow is a 400 and nothing is touched.
    /// The funnel keeps its own check — it is the authority, this is only the early refusal.
    /// </summary>
    /// <remarks>
    /// Scope is the caller's own body and nothing else:
    /// <list type="bullet">
    /// <item>Manual trigger only — a System hop's payload is not a client error to answer with 400.</item>
    /// <item>No transition mapping — the mapping's output is what gets written, the flow author decides
    /// it, and previewing it would mean running the script before admission.</item>
    /// <item>Not a request the parent relays to its active SubFlow — the leaf records the body and
    /// previews it against its own master schema.</item>
    /// </list>
    /// </remarks>
    private Task<Result> PreviewMasterSchemaAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (instanceDataWriteService is null
            || context.Data is null
            || context.Trigger != TriggerType.Manual
            || context.Workflow?.Schema is null
            || context.Instance is null
            || context.Transition?.Mapping is not null
            || SubflowForwardRule.WillForward(context))
            return Task.FromResult(Result.Ok());

        return instanceDataWriteService.PreviewAppendAsync(
            context.Instance,
            new JsonData(context.Data),
            context.Workflow,
            ResolveCulture(context.Headers),
            cancellationToken);
    }

    /// <inheritdoc />
    /// <summary>
    /// Validates transition data against JSON schema.
    /// Chains GetSchemaAsync Result into validation.
    /// </summary>
    public async Task<Result> ValidateSchemaAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        // x-encryption: a request may not introduce a value carrying a reserved prefix (ENCRYPTED:, HASHED:) —
        // only send back the value already stored at the same path. Refused here, before admission, so a client
        // error is a 400 and never reaches the write funnel inside the pipeline (where it would fault the
        // instance). The funnel keeps its own check for values produced by mappings.
        if (context.DataElement is { } payload && EncryptedValueFormat.MayContainReserved(payload.GetRawText()) &&
            EncryptedValueFormat.FindIntroducedToken(payload, context.Instance?.LatestData?.Data.JsonElement) is { } path)
        {
            return Result.Fail(WorkflowErrors.EncryptedValueReserved(path));
        }

        // sys-version-registry: a pointer matching no published version would fail closed every start of the
        // flow, so it is refused here — with the other request rejections, before admission reserves Busy and
        // before the pipeline runs (a failing pipeline step would fault the registry instance instead).
        // Start (ValidateStartTransitionAsync) and every transition accept both pass through here.
        if (string.Equals(context.WorkflowKey, RuntimeSysSchemaInfo.VersionRegistry, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(context.Instance?.Key))
        {
            var guardResult = await versionRegistryWriteGuard.ValidateAsync(
                context.Domain, context.Instance!.Key!, context.DataElement, cancellationToken);
            if (!guardResult.IsSuccess)
                return guardResult;
        }

        if (context.Transition is null)
            return Result.Ok();

        // Defensive: without the workflow and instance nothing can be resolved — no schema declared
        // accepts, a declared one fails closed.
        if (context.Workflow is null || context.Instance is null)
            return context.Transition.HasSchema
                ? Result.Fail(WorkflowErrors.TransitionSchemaNotMatched(context.TransitionKey))
                : Result.Ok();

        // Rule-based selection: the first matching `schemas` entry wins; none matching is a rejection.
        var resolved = await schemaResolver.ResolveAsync(
            context.Workflow, context.Instance, context.Transition, context.Headers, context.DataElement, cancellationToken);
        if (!resolved.IsSuccess)
            return Result.Fail(resolved.Error);
        if (resolved.Value is null)
            return Result.Ok();

        var schemaResult = await componentCacheStore.GetSchemaAsync(
            resolved.Value, cancellationToken);

        if (!schemaResult.IsSuccess)
            return Result.Fail(schemaResult.Error);

        using var schemaActivity = PipelineStepActivityHelper.StartTransitionActivity(
            "Schema.Validate", context.TransitionKey);
        var validationResult = schemaValidator.Validate(
            schemaResult.Value!.Schema,
            context.DataElement,
            CreateSchemaValidationOptions(context.Headers));
        if (!validationResult.IsSuccess)
            schemaActivity?.SetStatus(ActivityStatusCode.Error, validationResult.Error.Message);

        return validationResult;
    }

    private static SchemaValidationOptions CreateSchemaValidationOptions(IReadOnlyDictionary<string, string?>? headers)
    {
        return new SchemaValidationOptions(
            Culture: ResolveCulture(headers),
            IncludeVocabularyDetails: true,
            CustomValidationEnabled: true);
    }

    private static string ResolveCulture(IReadOnlyDictionary<string, string?>? headers)
        => LanguageResolver.ResolveCulture(headers);

    /// <inheritdoc />
    /// <summary>
    /// Validates a start transition.
    /// Railway chain: Get Initial State → Build Context → Validate
    /// </summary>
    public async Task<Result> ValidateStartTransitionAsync(
        Definitions.Workflow workflow,
        Instance instance,
        Transition transition,
        object? data,
        IRuntimeInfoProvider runtimeInfoProvider,
        IReadOnlyDictionary<string, string?>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var contextResult = workflow.GetInitialState()
            .Map(initialState => BuildStartTransitionContext(
                workflow, instance, transition, initialState,
                data, runtimeInfoProvider, headers));

        if (!contextResult.IsSuccess)
            return Result.Fail(contextResult.Error);

        return await ValidateAsync(contextResult.Value!, cancellationToken);
    }

    /// <summary>
    /// Builds a TransitionExecutionContext for start transition validation.
    /// </summary>
    private static TransitionExecutionContext BuildStartTransitionContext(
        Definitions.Workflow workflow,
        Instance instance,
        Transition transition,
        State initialState,
        object? data,
        IRuntimeInfoProvider runtimeInfoProvider,
        IReadOnlyDictionary<string, string?>? headers)
    {
        var (traceId, spanId) = InitializeTelemetry();

        return new TransitionExecutionContext
        {
            // Identity
            Domain = runtimeInfoProvider.Domain,
            InstanceId = instance.Id,
            WorkflowKey = workflow.Key,
            TransitionKey = transition.Key,
            Trigger = TriggerType.Manual, // Start transitions are always manual
            Actor = ExecutionActor.User, // Start transitions are always user-initiated
            CorrelationId = Guid.NewGuid().ToString("N"),
            CausationId = null,
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            ChainDepth = 0,
            RequestedAt = DateTimeOffset.UtcNow,

            // Definitions
            Workflow = workflow,
            Current = initialState,
            Transition = transition,

            // Instance state
            Instance = instance,
            Data = data,
            HopInput = data,

            // Flags
            IsReentry = false, // Start transitions are never re-entry

            // Telemetry
            TraceId = traceId,
            SpanId = spanId,
            Headers = headers ?? new Dictionary<string, string?>()
        };
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

    /// <inheritdoc />
    /// <summary>
    /// Validates trigger type rules and execution context.
    /// </summary>
    public Task<Result> ValidateTriggerTypeAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = context.Trigger switch
        {
            TriggerType.Manual => ValidateManualTrigger(context),
            TriggerType.Automatic => ValidateAutomaticTrigger(context),
            TriggerType.Scheduled => ValidateScheduledTrigger(context),
            TriggerType.Event => ValidateEventTrigger(context),
            _ => Result.Fail(Error.NotSupported(
                "UnsupportedTriggerType",
                $"Trigger type {context.Trigger} is not supported"))
        };

        return Task.FromResult(result);
    }

    /// <summary>
    /// Validates manual trigger execution rules.
    /// Manual transitions should be initiated by User actors.
    /// </summary>
    private static Result ValidateManualTrigger(TransitionExecutionContext context)
    {
        // Actor should be User for manual transitions
        if (context.Actor != ExecutionActor.User)
        {
            return Result.Fail(WorkflowErrors.InvalidActorForManualTransition(
                context.InstanceId,
                context.Actor));
        }

        return Result.Ok();
    }

    /// <summary>
    /// Validates automatic trigger execution rules.
    /// Automatic transitions should be initiated by System actors and respect chain depth limits.
    /// </summary>
    private static Result ValidateAutomaticTrigger(TransitionExecutionContext context)
    {
        // Actor should be System for automatic transitions
        if (context.Actor != ExecutionActor.System)
        {
            return Result.Fail(WorkflowErrors.InvalidActorForAutomaticTransition(
                context.InstanceId,
                context.Actor));
        }

        // Chain depth check (defense in depth - also checked in pipeline)
        const int maxChainDepth = 50;
        if (context.ChainDepth > maxChainDepth)
        {
            return Result.Fail(WorkflowErrors.TransitionChainDepthExceeded(
                context.ChainDepth,
                maxChainDepth,
                context.TransitionKey));
        }

        return Result.Ok();
    }

    /// <summary>
    /// Validates scheduled trigger execution rules.
    /// Scheduled transitions should be initiated by System actors.
    /// Sets SkipImmediateExecution flag if not a re-entry call (initial schedule request).
    /// </summary>
    private static Result ValidateScheduledTrigger(TransitionExecutionContext context)
    {
        // Actor should be System for scheduled transitions
        if (context.Actor != ExecutionActor.System)
        {
            return Result.Fail(WorkflowErrors.InvalidActorForScheduledTransition(
                context.InstanceId,
                context.Actor));
        }

        // Skip immediate execution if not re-entry (first call = schedule only)
        // When background job executes (IsReentry=true), it will execute immediately
        if (!context.IsReentry)
        {
            context.SkipImmediateExecution = true;
        }

        return Result.Ok();
    }

    /// <summary>
    /// Validates event trigger execution rules.
    /// Event transitions are the exclusive entry point of the event subsystem and are dispatched under
    /// the <see cref="ExecutionActor.System"/> actor.
    /// </summary>
    private static Result ValidateEventTrigger(TransitionExecutionContext context)
    {
        if (context.Actor != ExecutionActor.System)
        {
            return Result.Fail(WorkflowErrors.InvalidActorForEventTransition(
                context.InstanceId,
                context.Actor));
        }

        return Result.Ok();
    }
}
