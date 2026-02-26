using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.Aspects;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that detects updateData transitions and performs data update operations.
/// This step runs early (Preflight order) to skip normal transition processing for data updates.
/// </summary>
public sealed class HandleUpdateDataPreflightStep(
    ILogger<HandleUpdateDataPreflightStep> logger,
    IInstanceTransitionRepository instanceTransitionRepository,
    IInstanceRepository instanceRepository,
    IGuidGenerator guidGenerator,
    ITransitionDataMapper transitionDataMapper,
    IRuntimeInfoProvider runtimeInfoProvider) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.CheckParentUpdateDataTransition;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(HandleUpdateDataPreflightStep)}");

        // Skip if not an updateData transition
        if (!IsCurrentStateSubFlow(context) ||!context.IsUpdateDataTransition())
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Railway chain: Log detection -> Validate -> Update data -> Create skip outcome
        return await Result.Ok(context)
            .Ensure(
                ctx => !ctx.Instance.IsCompleted,
                CreateAlreadyCompletedError(context))
            .BindAsync(_ => UpdateDataAsync(context, cancellationToken))
            .Tap(_ => logger.UpdateDataTransitionDetected(context.InstanceId))
            .Tap(_ => logger.UpdateDataSkipToFinish(context.InstanceId))
            .Map(_ => CreateSkipOutcome());
    }

        /// <summary>
    /// Checks if current state is a SubFlow state.
    /// </summary>
    private static bool IsCurrentStateSubFlow(TransitionExecutionContext context)
        => context.Current.StateType == StateType.SubFlow;

    /// <summary>
    /// Creates error for already completed instance.
    /// </summary>
    private Error CreateAlreadyCompletedError(TransitionExecutionContext context)
    {
        logger.UpdateDataInstanceAlreadyCompleted(context.InstanceId, context.Instance.Status.Description);
        return ExecutionErrors.InstanceAlreadyCompleted(context.InstanceId, context.Instance.Status.Description);
    }

    /// <summary>
    /// Updates data for UpdateData transition.
    /// Performs the same operations as CreateTransitionRecordStep (41-52).
    /// </summary>
    private async Task<Result<object?>> UpdateDataAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Build transition info
        var transitionKey = GetTransitionKey(context);
        var (instanceTransition, transition) = CreateInstanceTransition(context, transitionKey);

        // Railway chain: Map data -> Add to instance -> Validate key uniqueness -> Persist
        return await MapTransitionDataAsync(context, transition, cancellationToken)
            .Tap(mappedData => AddMappedDataToInstance(context, mappedData, transition))
            .BindAsync(_ => ValidateAndSetInstanceKeyAsync(context, cancellationToken))
            .TapAsync(_ => instanceRepository.UpdateAsync(context.Instance, false, cancellationToken))
            .TapAsync(_ =>
                instanceTransitionRepository.InsertAsync(instanceTransition, saveChanges: true, cancellationToken))
            .Tap(_ => UpdateContextItems(context, instanceTransition.Id))
            .Map(_ => (object?)null);
    }

    /// <summary>
    /// Gets the transition key from context items or uses the default transition key.
    /// </summary>
    private static string GetTransitionKey(TransitionExecutionContext context)
    {
        return context.Items.TryGetValue("NextTransitionKey", out var v) &&
               v is string next &&
               !string.IsNullOrEmpty(next)
            ? next
            : context.TransitionKey;
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
    }

    /// <summary>
    /// Validates that the instance key is unique among active instances and sets it if valid.
    /// </summary>
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
    /// Updates context items with transition record ID.
    /// </summary>
    private static void UpdateContextItems(TransitionExecutionContext context, Guid transitionId)
    {
        context.Items["TransitionRecordId"] = transitionId;
        context.Items.Remove("NextTransitionKey");
    }

    /// <summary>
    /// Creates outcome to skip to Finalize step.
    /// </summary>
    private static StepOutcome CreateSkipOutcome()
    {
        return new StepOutcome
        {
            SkipToOrder = LifecycleOrder.Finalize
        };
    }
}


