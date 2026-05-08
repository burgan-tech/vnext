using System.Diagnostics;
using BBT.Aether.Guids;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using System.Text.Json;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that creates and persists the transition record.
/// This step tracks the transition attempt and provides audit trail.
/// </summary>
public sealed class CreateTransitionRecordStep(
    IInstanceTransitionRepository instanceTransitionRepository,
    IInstanceRepository instanceRepository,
    IGuidGenerator guidGenerator,
    ITransitionDataMapper transitionDataMapper,
    IRuntimeInfoProvider runtimeInfoProvider) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.CreateTransition;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(CreateTransitionRecordStep)}");

        // Skip for SubFlow resume - transition record already exists
        if (context.Directives.IsSubFlowResume)
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Build transition info
        var transitionKey = GetTransitionKey(context);
        var (instanceTransition, transition) = CreateInstanceTransition(context, transitionKey);

        // Railway chain: Map data -> Add to instance -> Validate key uniqueness -> Persist
        return await MapTransitionDataAsync(context, transition, cancellationToken)
            .Tap(mappedData => AddMappedDataToInstance(context, mappedData, transition))
            .BindAsync(_ => ValidateAndSetInstanceKeyAsync(context, cancellationToken))
            .TapAsync(_ => instanceRepository.UpdateAsync(context.Instance, true, cancellationToken))
            .TapAsync(_ =>
                instanceTransitionRepository.InsertAsync(instanceTransition, saveChanges: true, cancellationToken))
            .Tap(_ => UpdateContextItems(context, instanceTransition))
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
    /// Adds mapped data to instance if available.
    /// </summary>
    private void AddMappedDataToInstance(
        TransitionExecutionContext context,
        object? mappedData,
        Definitions.Transition? transition)
    {
        if (mappedData != null)
        {
            context.Instance.AddData(
                guidGenerator.Create(),
                new JsonData(mappedData),
                transition?.VersionStrategy);
        }

        if (context.Tags != null)
        {
            context.Instance.AddTags(context.Tags);
        }

        if (!string.IsNullOrWhiteSpace(context.Stage))
        {
            context.Instance.SetStage(context.Stage);
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
    private static void UpdateContextItems(TransitionExecutionContext context, InstanceTransition instanceTransition)
    {
        context.Items["TransitionRecordId"] = instanceTransition.Id;
        context.Items["InstanceTransition"] = instanceTransition;
        context.Items.Remove("NextTransitionKey");
    }
}