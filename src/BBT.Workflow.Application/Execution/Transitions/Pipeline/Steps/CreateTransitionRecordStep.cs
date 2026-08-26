using BBT.Aether.Guids;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
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
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<CreateTransitionRecordStep> logger) : ITransitionStep
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
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
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
        if (context.RetryOfTransitionRecordId is { } retriedRecordId)
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
        return await MapTransitionDataAsync(context, transition, cancellationToken)
            .BindAsync(mappedData => ValidateAndSetInstanceKeyAsync(context, cancellationToken)
                .MapAsync(_ => mappedData))
            .TapAsync(mappedData => AppendMappedDataAsync(
                context, mappedData, transition, instanceTransition, cancellationToken))
            .TapAsync(_ => instanceRepository.UpdateAsync(context.Instance, false, cancellationToken))
            .TapAsync(_ => isReusedRecord
                ? instanceTransitionRepository.UpdateAsync(instanceTransition, true, cancellationToken)
                : instanceTransitionRepository.InsertAsync(instanceTransition, saveChanges: true, cancellationToken))
            .Tap(_ => UpdateContextItems(context, instanceTransition, isReusedRecord))
            .Map(_ => StepOutcome.Continue());
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
            new JsonData(JsonSerializer.Serialize(context.Headers)));

        var state = context.Workflow.GetState(context.Instance.GetCurrentState).Value!;
        var transition = context.Workflow.ResolveTransition(transitionKey, state);

        return (instanceTransition, transition);
    }

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
            runtimeInfoProvider,
            context.Headers,
            cancellationToken);
    }

    /// <summary>
    /// Applies non-data field mutations (tags/stage), persists the mapped data IMMEDIATELY
    /// through the InstanceData write service (identity computed under the row lock; identical
    /// merged content dedups to no row), and updates the transition body when a mapping script
    /// was applied.
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

        if (mappedData != null)
        {
            await instanceDataWriteService.AppendAsync(
                context.Instance,
                new JsonData(mappedData),
                transition?.VersionStrategy,
                cancellationToken);

            if (transition?.Mapping is not null)
            {
                instanceTransition.SetBody(new JsonData(mappedData));
            }
        }
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
        context.Items["TransitionRecordId"] = instanceTransition.Id;
        context.Items["InstanceTransition"] = instanceTransition;
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