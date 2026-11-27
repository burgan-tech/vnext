using BBT.Aether.Guids;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Cancellation;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Files;
using BBT.Workflow.Instances;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that creates and persists the transition record.
/// This step tracks the transition attempt and provides audit trail.
/// </summary>
public sealed class CreateTransitionRecordStep(
    IInstanceTransitionRepository instanceTransitionRepository,
    IInstanceRepository instanceRepository,
    IInstanceDataWriteService instanceDataWriteService,
    IGuidGenerator guidGenerator,
    ITransitionDataMapper transitionDataMapper,
    IFileOffloadService fileOffloadService,
    ILogger<CreateTransitionRecordStep> logger,
    Persistence.IBookkeepingWriter? bookkeepingWriter = null,
    Headers.IHeaderExposureDenyList? headerExposureDenyList = null) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.CreateTransition;

    /// <inheritdoc />
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Skip for SubFlow resume - transition record already exists
        if (context.Directives.IsSubFlowResume)
        {
            return Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork());
        }

        // Build transition info
        var transitionKey = GetTransitionKey(context);
        var (instanceTransition, transition) = CreateInstanceTransition(context, transitionKey);

        // Retry re-entry: reuse the ORIGINAL transition record instead of inserting a fresh one,
        // so the task journal (InstanceTask rows keyed by transition record id) lines up and
        // already-completed tasks are bypassed rather than re-running their side effects. When
        // the original record cannot be found, fall back to normal creation — a retry must not
        // fail on a missing audit row.
        var isReusedRecord = false;
        var suppressesHistory = context.Workflow.SuppressesHistory;
        if (context.RetryOfTransitionRecordId is { } retriedRecordId && !suppressesHistory)
        {
            var original = await instanceTransitionRepository.FindAsync(
                retriedRecordId, true, cancellationToken);
            if (original is not null)
            {
                instanceTransition = original;
                isReusedRecord = true;
                logger.TransitionRecordReusedForRetry(context.InstanceId, retriedRecordId, transitionKey);
            }
        }

        // Railway chain: Map data -> Validate key uniqueness -> Persist data (immediately,
        // through the write service — row identity computed under the per-instance row lock)
        // -> Persist the record. Non-data field changes (tags/stage/key) ride the same
        // SaveChanges the write service (or the record persist) performs.
        var recordsFailure = !isReusedRecord && !suppressesHistory && IsRetryableHop(context);
        return await MapAndAppendAsync(context, transition, instanceTransition, recordsFailure, cancellationToken)
            .TapAsync(_ => PersistTransitionRecordAsync(
                context.Instance, instanceTransition, isReusedRecord, suppressesHistory, cancellationToken))
            .Tap(_ => UpdateContextItems(context, instanceTransition, isReusedRecord))
            .Map(_ => StepOutcome.Continue());
    }

    /// <summary>
    /// A hop <c>retry</c> can re-run: one a caller asked for — manual by definition, updateData and the
    /// reserved cancel included. Retry re-runs a hop as a manual request, which an automatic, scheduled or
    /// event hop refuses (vnext#1338); an error-boundary transition is chosen by the engine, not requested.
    /// </summary>
    /// <remarks>
    /// <see cref="TransitionExecutionContext.Trigger"/> is the DEFINITION's trigger, and that is the right test
    /// here, not the inbound one the profile resolves from: retry's admission
    /// (<c>ActorAuthorizationSpecification</c>) judges the definition, whatever trigger first ran the hop.
    /// </remarks>
    private static bool IsRetryableHop(TransitionExecutionContext context)
        => context.Trigger == TriggerType.Manual && !context.IsErrorBoundaryTransition;

    /// <summary>
    /// Maps the data, claims the instance key and writes the data: the part of the hop that runs before its
    /// record is saved. A failure here — the mapping throws, the merged data fails the master schema, the key
    /// is taken — faults the instance with no record of the hop, and retry, which re-runs the latest open
    /// record, then found nothing to re-run (<c>Instance:100028</c>, vnext#1252). So when
    /// <paramref name="recordsFailure"/> is set, the record is saved unfinished before the failure travels on,
    /// a thrown exception included.
    /// </summary>
    private async Task<Result<object?>> MapAndAppendAsync(
        TransitionExecutionContext context,
        Definitions.Transition? transition,
        InstanceTransition instanceTransition,
        bool recordsFailure,
        CancellationToken cancellationToken)
    {
        if (!recordsFailure)
            return await MapAndAppendCoreAsync(context, transition, instanceTransition, cancellationToken);

        try
        {
            var result = await MapAndAppendCoreAsync(context, transition, instanceTransition, cancellationToken);
            if (!result.IsSuccess)
                await RecordFailedHopAsync(context, instanceTransition);

            return result;
        }
        catch
        {
            await RecordFailedHopAsync(context, instanceTransition);
            throw;
        }
    }

    private Task<Result<object?>> MapAndAppendCoreAsync(
        TransitionExecutionContext context,
        Definitions.Transition? transition,
        InstanceTransition instanceTransition,
        CancellationToken cancellationToken)
        => MapTransitionDataAsync(context, transition, cancellationToken)
            .BindAsync(mappedData => ValidateAndSetInstanceKeyAsync(context, cancellationToken)
                .MapAsync(_ => mappedData))
            .TapAsync(mappedData => AppendMappedDataAsync(
                context, mappedData, transition, instanceTransition, cancellationToken));

    /// <summary>
    /// Saves the record of a hop that failed before its own save, unfinished — the shape retry looks for. It
    /// holds what the hop was asked to do (key, source state, request body and headers); the incident the fault
    /// writes next says why it failed. Best effort, on a bounded token of its own: the hop has already failed,
    /// often because the caller's token was cancelled, and the fault that follows must happen whether or not
    /// this write does.
    /// </summary>
    /// <remarks>
    /// Saved on the hop's own unit of work. Nothing a failure here leaves behind can poison that save: the data
    /// write goes through its own Npgsql batch (<c>InstanceDataWriteService</c>), not the change tracker, and the
    /// key check only reads. The one thing the save also flushes is the instance's key/tags/stage, which this
    /// unit of work's commit wrote after the fault anyway. A save that fails is forgotten, not left Added.
    /// </remarks>
    private async Task RecordFailedHopAsync(TransitionExecutionContext context, InstanceTransition instanceTransition)
    {
        using var compensation = CompensationCancellation.Create();
        try
        {
            await instanceTransitionRepository.InsertAsync(instanceTransition, true, compensation.Token);
            logger.FailedTransitionRecorded(context.InstanceId, instanceTransition.TransitionId, instanceTransition.Id);
        }
        catch (Exception exception)
        {
            // A failed save leaves the record Added on this unit of work; its commit, after the fault, would replay
            // the INSERT and fail again — or collide with a row that did reach the database before a timeout.
            await instanceTransitionRepository.ForgetAsync(instanceTransition);
            logger.FailedTransitionRecordNotSaved(context.InstanceId, instanceTransition.TransitionId, exception);
        }
    }

    /// <summary>
    /// Covers EF change detection and SaveChanges preparation as well as the command spans emitted
    /// by the provider. Without this parent, the CPU before the transition INSERT appears as a gap.
    /// </summary>
    private async Task PersistTransitionRecordAsync(
        Instance instance,
        InstanceTransition instanceTransition,
        bool isReusedRecord,
        bool suppressesHistory,
        CancellationToken cancellationToken)
    {
        using var activity = PipelineStepActivityHelper.StartTransitionActivity(
            "TransitionRecord.Persist", instanceTransition.TransitionId);

        // history: none (vnext#1006): the record stays in memory (scripts still read
        // CurrentTransition); only the instance's own field changes (key, tags, stage) are saved.
        if (suppressesHistory)
        {
            await instanceRepository.UpdateAsync(instance, true, cancellationToken);
            return;
        }

        await instanceRepository.UpdateAsync(instance, false, cancellationToken);

        // Bookkeeping: the record (and the instance field changes riding the same save) may commit
        // asynchronously under WorkflowExecution:Bookkeeping — see BookkeepingWriteOptions.
        Func<CancellationToken, Task> write = isReusedRecord
            ? ct => instanceTransitionRepository.UpdateAsync(instanceTransition, true, ct)
            : ct => instanceTransitionRepository.InsertAsync(instanceTransition, saveChanges: true, ct);

        if (bookkeepingWriter is null)
            await write(cancellationToken);
        else
            await bookkeepingWriter.RunAsync(write, cancellationToken);
    }

    /// <summary>
    /// Gets the transition key from context items or uses the default transition key.
    /// Well-known virtual keys (e.g. "$timeout") are resolved to their configured key values
    /// so the audit record stores the meaningful key instead of the virtual placeholder.
    /// </summary>
    private static string GetTransitionKey(TransitionExecutionContext context)
    {
        var rawKey = context.Items.TryGetValue("NextTransitionKey", out var v) &&
               v is string next &&
               !string.IsNullOrEmpty(next)
            ? next
            : context.TransitionKey;

        // $timeout resolves through the EFFECTIVE timeout, not the workflow's own. A SubFlow child
        // started with a parent-supplied subflow.timeout_override carries no timeout in its own
        // definition, and Workflow.ResolveWellKnownKey throws TimeoutNotConfiguredForWorkflowException
        // for it — HERE, at order 20, long before ApplyTimeoutStateStep (38) could resolve the
        // override. Measured on the bench: the child was left Busy in its waiting state with the
        // job already marked processed, so the deadline both failed to fire AND stranded the
        // instance. Same resolver the arm, the fire path and the state function's timeout block use.
        if (string.Equals(rawKey, WellKnownTransitionKeys.Timeout, StringComparison.OrdinalIgnoreCase))
        {
            var effectiveTimeout = context.Instance.ResolveEffectiveTimeout(context.Workflow);
            if (effectiveTimeout is not null)
                return effectiveTimeout.Key;
        }

        return context.Workflow.ResolveTransitionKey(rawKey);
    }

    /// <summary>
    /// Creates the instance transition record and finds the transition definition.
    /// </summary>
    private (InstanceTransition InstanceTransition, Definitions.Transition? Transition) CreateInstanceTransition(
        TransitionExecutionContext context,
        string transitionKey)
    {
        var instanceTransition = InstanceTransition.Create(
            guidGenerator.Create(),
            context.InstanceId,
            transitionKey,
            context.Instance.GetCurrentState,
            context.Trigger,
            new JsonData(context.Data),
            new JsonData(JsonSerializer.Serialize(RecordedHeaders(context))));

        var state = context.Workflow.GetState(context.Instance.GetCurrentState).Value!;
        var transition = context.Workflow.ResolveTransition(transitionKey, state);

        return (instanceTransition, transition);
    }

    /// <summary>
    /// The request headers the transition record keeps: <c>Workflow:HeaderExposure:Deny</c> names are never written.
    /// Only the record is reduced — <see cref="TransitionExecutionContext.Headers"/> stays as it arrived, because the
    /// runtime's own role, identity and credential reads use it for the rest of the hop.
    /// </summary>
    private IReadOnlyDictionary<string, string?> RecordedHeaders(TransitionExecutionContext context) =>
        headerExposureDenyList is null or { IsEmpty: true } ? context.Headers : headerExposureDenyList.Filter(context.Headers);

    /// <summary>
    /// Maps transition data using the data mapper service.
    /// </summary>
    private Task<Result<object?>> MapTransitionDataAsync(
        TransitionExecutionContext context,
        Definitions.Transition? transition,
        CancellationToken cancellationToken)
    {
        return transitionDataMapper.MapTransitionDataAsync(
            context.Data,
            transition,
            context.Workflow,
            context.Instance,
            context.Headers,
            onScriptFailure: ex => context.FailureCause = ex,
            actor: context.Actor,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Applies non-data field mutations (tags/stage), persists the mapped data IMMEDIATELY
    /// through the InstanceData write service (identity computed under the row lock; identical
    /// merged content dedups to no row), and updates the transition body when a mapping script
    /// or a declarative output block was applied.
    /// </summary>
    private async Task AppendMappedDataAsync(
        TransitionExecutionContext context,
        object? mappedData,
        Definitions.Transition? transition,
        InstanceTransition instanceTransition,
        CancellationToken cancellationToken)
    {
        if (context.Tags != null)
        {
            context.Instance.AddTags(context.Tags);
        }

        if (!string.IsNullOrWhiteSpace(context.Stage))
        {
            context.Instance.SetStage(context.Stage);
        }

        if (mappedData != null && transition?.HasDataMapping == true)
        {
            mappedData = await OffloadMappedFilesAsync(context, mappedData, cancellationToken);
        }

        if (mappedData != null)
        {
            // updateData is the one transition whose outcome writes nothing to the instance row: it
            // does not own the status (no Busy flip of its own) and ChangeStateStep leaves its
            // ChangeState($self) a no-op save. So its data rows — this body, and its tasks' output
            // (TaskExecutionEngine) — are the modification and stamp Instances.ModifiedAt: one extra
            // UPDATE per row, inside that row's insert batch. An automatic transition it hands off to
            // stamps again through its own Busy flip; that is a later, separate modification. Every
            // other transition's appends ride on its own outcome stamp.
            await instanceDataWriteService.AppendAsync(
                context.Instance,
                new JsonData(mappedData),
                transition?.VersionStrategy,
                cancellationToken,
                context.Workflow,
                touchInstance: context.IsUpdateDataTransition());

            // The mapping's result (a script's, or a declarative output block's delta) replaces the record body.
            if (transition?.HasDataMapping == true)
            {
                instanceTransition.SetBody(new JsonData(mappedData));
            }
        }
    }

    /// <summary>
    /// x-storage defence in depth (spec §3): <c>content</c> a transition mapping produces is offloaded (Trusted)
    /// before it reaches the data funnel or the transition record body. Runs only for a mapped transition (script or
    /// declarative output block): the
    /// field lookup loads the master schema through the component cache once; no <c>x-storage</c> field ⇒ no
    /// conversion and no span. Otherwise a read-only probe over the element decides — only a <c>content</c> or a
    /// <c>file</c> member at an <c>x-storage</c> path opens the <c>Files.Offload</c> span and calls the offload (content is
    /// stored, a file handle is validated). A failure throws (<see cref="FileOffloadFailure"/>) and follows the
    /// pipeline's normal error path; a master schema that cannot be loaded throws
    /// <see cref="FileSchemaUnavailableException"/> when the mapped data could carry a file.
    /// </summary>
    private async Task<object?> OffloadMappedFilesAsync(
        TransitionExecutionContext context,
        object mappedData,
        CancellationToken cancellationToken)
    {
        var element = mappedData is JsonElement e
            ? e
            : JsonSerializer.SerializeToElement(mappedData, JsonSerializerConstants.JsonOptions);
        // Nothing file-shaped anywhere ⇒ nothing to offload or validate whatever the schema says: no schema load.
        if (element.ValueKind != JsonValueKind.Object || !FileNodeWalker.AnyFileShapedNode(element))
            return mappedData;

        var fieldsResult = await fileOffloadService.GetFieldsAsync(context.Workflow, cancellationToken);
        if (fieldsResult.IsSuccess && fieldsResult.Value!.Count == 0)
            return mappedData;

        // Master schema unavailable: fail closed only when the mapped data could carry a file.
        var resolved = FileStorageFields.ForPayload(fieldsResult, element);
        if (!resolved.IsSuccess)
            throw new FileSchemaUnavailableException(resolved.Error.Target ?? context.Workflow.Schema?.Key ?? string.Empty);
        var fields = resolved.Value!;

        // Read-only probe: no content and no file reference at any x-storage path ⇒ nothing to offload or validate,
        // no mutable DOM, no span.
        if (!FileNodeWalker.AnyContentOrFile(element, fields))
            return mappedData;

        using var activity = PipelineStepActivityHelper.StartTransitionActivity("Files.Offload", context.TransitionKey);
        var offload = await fileOffloadService.OffloadAsync(new FileOffloadRequest(
            context.Workflow, context.Instance.Id, element, LatestData: null, FileOffloadMode.Trusted, fields), cancellationToken);
        if (!offload.IsSuccess)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, offload.Error.Code);
            throw FileOffloadFailure.ToException(offload.Error);
        }

        return offload.Value!.Changed ? offload.Value.Payload : mappedData;
    }

    /// <summary>
    /// Validates that the instance key is unique among active instances and sets it if valid.
    /// </summary>
    /// <param name="context">The transition execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success if no duplicate key exists, failure with DuplicateInstanceKey error otherwise.</returns>
    private async Task<Result<object?>> ValidateAndSetInstanceKeyAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        // If no key is provided, skip validation
        if (string.IsNullOrWhiteSpace(context.InstanceKey) || context.Instance.HasKey)
        {
            return Result<object?>.Ok(null);
        }

        // Check if another active instance already has this key
        var isDuplicate = await instanceRepository.AnyActiveByKeyAsync(
            context.InstanceKey,
            context.InstanceId,
            cancellationToken);

        if (isDuplicate)
        {
            return Result<object?>.Fail(
                ExecutionErrors.DuplicateInstanceKey(context.InstanceKey, context.InstanceId));
        }

        // Set the key on the instance
        context.Instance.SetKey(context.InstanceKey);
        return Result<object?>.Ok(null);
    }

    /// <summary>
    /// Updates context items with transition record ID and the InstanceTransition for ScriptContext.CurrentTransition.
    /// </summary>
    private static void UpdateContextItems(
        TransitionExecutionContext context,
        InstanceTransition instanceTransition,
        bool isReusedRecord)
    {
        context.Items[WellKnownItems.TransitionRecordId] = instanceTransition.Id;
        context.Items[WellKnownItems.InstanceTransition] = instanceTransition;
        // A FRESH record id cannot have InstanceTask journal rows yet, so the task engine may skip
        // its per-task idempotency probe; a reused (retry) record keeps the probe, which is what
        // lets already-persisted task rows be found and reused instead of duplicated.
        context.Items[TransitionRecordFreshKey] = !isReusedRecord;
        context.Items.Remove("NextTransitionKey");
    }

    /// <summary>
    /// Context item: true when this pipeline run INSERTED the transition record (no task journal
    /// rows can exist for its id), false when a retry reused the original record.
    /// </summary>
    public const string TransitionRecordFreshKey = "TransitionRecordCreatedFresh";
}
