using BBT.Aether.Guids;
using BBT.Workflow.Domain;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Validation;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that creates and persists the transition record.
/// This step tracks the transition attempt and provides audit trail.
/// Uses Result pattern for exception-free error handling.
/// </summary>
public sealed class CreateTransitionRecordStep(
    IInstanceTransitionRepository instanceTransitionRepository,
    IInstanceRepository instanceRepository,
    IGuidGenerator guidGenerator,
    ITransitionDataMapper transitionDataMapper,
    IRuntimeInfoProvider runtimeInfoProvider,
    IInstanceDataValidationService instanceDataValidationService,
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
            logger.LogDebug("Skipping transition record creation for SubFlow resume on instance {InstanceId}",
                context.InstanceId);
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }
        
        logger.LogDebug("Creating transition record for {TransitionKey} on instance {InstanceId}",
            context.TransitionKey, context.InstanceId);

        return await ResultExtensions.TryAsync<StepOutcome>(async ct =>
            {
                var transitionKey = context.Items.TryGetValue("NextTransitionKey", out var v) && v is string next &&
                                    !string.IsNullOrEmpty(next)
                    ? next
                    : context.TransitionKey;

                // Create the transition record
                var instanceTransition = InstanceTransition.Create(
                    guidGenerator.Create(),
                    context.InstanceId,
                    transitionKey,
                    context.Instance.GetCurrentState,
                    new JsonData(context.Data),
                    new JsonData(JsonSerializer.Serialize(context.Headers))
                );

                var transition = context.Workflow.FindTransition(transitionKey);

                // Map transition data using optional mapping script
                var mappedDataResult = await transitionDataMapper.MapTransitionDataAsync(
                    context.Data,
                    transition,
                    context.Workflow,
                    context.Instance,
                    runtimeInfoProvider,
                    context.Headers,
                    ct);

                if (!mappedDataResult.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Transition mapping failed: {mappedDataResult.Error.Message}");
                }

                if (mappedDataResult.Value != null)
                {
                    context.Instance.AddData(
                        guidGenerator.Create(),
                        new JsonData(mappedDataResult.Value!),
                        transition?.VersionStrategy
                    );
                    // Validate instance data after data change (OnDataChange mode)
                    var validationResult = await instanceDataValidationService.ValidateOnDataChangeAsync(
                        context.Instance,
                        context.Workflow,
                        logger,
                        ct);
                    
                    if (!validationResult.IsSuccess)
                    {
                        throw new InvalidOperationException(
                            $"Instance data validation failed: {validationResult.Error.Message}");
                    }
                }
                
                await instanceRepository.UpdateAsync(context.Instance, true, ct);

                // Persist the record
                await instanceTransitionRepository.InsertAsync(instanceTransition, saveChanges: true, ct);

                // Store in context for other steps to use
                context.Items["TransitionRecordId"] = instanceTransition.Id;
                context.Items.Remove("NextTransitionKey");

                logger.LogDebug("Created transition record {TransitionId} for {TransitionKey}",
                    instanceTransition.Id, context.TransitionKey);

                return StepOutcome.Continue();
            },
            cancellationToken,
            ex => Error.Failure(
                WorkflowErrorCodes.ExecutionStepFailed,
                $"Failed to create transition record: {ex.Message}",
                ex.GetType().Name));
    }
}