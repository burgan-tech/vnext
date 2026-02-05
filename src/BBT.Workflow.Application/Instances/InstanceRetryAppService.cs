using BBT.Aether.Application.Services;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Instances;

/// <summary>
/// Application service for retrying faulted workflow instances.
/// Supports two scenarios:
/// 1. Instance is Faulted → retry the instance's incomplete transition
/// 2. Instance has a Faulted SubFlow → retry the SubFlow (delegated to gateway)
/// </summary>
public sealed class InstanceRetryAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IInstanceRepository instanceRepository,
    IInstanceTransitionRepository instanceTransitionRepository,
    IInstanceQueryGateway instanceQueryGateway,
    IInstanceRetryGateway instanceRetryGateway,
    IComponentCacheStore componentCacheStore,
    IWorkflowExecutionService workflowExecutionService,
    ILogger<InstanceRetryAppService> logger)
    : ApplicationService(serviceProvider), IInstanceRetryAppService
{
    /// <inheritdoc />
    public async Task<Result<RetryInstanceOutput>> RetryAsync(
        RetryInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        // Validate domain
        runtimeInfoProvider.Check(input.Domain);

        logger.InstanceRetryRequested(input.Instance, input.Workflow);

        // Step 1: Load instance with details (including correlations)
        var instanceResult = await instanceRepository.GetResultAsync(input.Instance, includeDetails: true, cancellationToken);
        if (!instanceResult.IsSuccess)
            return Result<RetryInstanceOutput>.Fail(instanceResult.Error);

        var instance = instanceResult.Value!;

        // Step 2: Scenario 1 - Instance itself is Faulted
        if (instance.Status.Equals(InstanceStatus.Faulted))
        {
            return await RetryFaultedInstanceAsync(instance, input, cancellationToken);
        }

        // Step 3: Scenario 2 - Check if instance has an active SubFlow
        var subflowCorrelation = instance.Subflow;
        if (subflowCorrelation == null)
        {
            // No SubFlow + Instance not Faulted → Error
            return Result<RetryInstanceOutput>.Fail(Error.Validation(
                WorkflowErrorCodes.InstanceNotFaulted,
                $"Instance {input.Instance} is not in faulted state. Current status: {instance.Status.Code}",
                input.Instance));
        }

        // Step 4: SubFlow exists - check if it's Faulted and retry it
        return await RetrySubFlowAsync(instance, subflowCorrelation, input, cancellationToken);
    }

    /// <summary>
    /// Retries a SubFlow that is in faulted state.
    /// Uses IInstanceQueryGateway to support cross-domain SubFlow queries.
    /// </summary>
    private async Task<Result<RetryInstanceOutput>> RetrySubFlowAsync(
        Instance parentInstance,
        InstanceCorrelation subflowCorrelation,
        RetryInstanceInput input,
        CancellationToken cancellationToken)
    {
        // Query SubFlow state via gateway (supports cross-domain)
        var subflowStateInput = new GetFunctionWithInstanceInput
        {
            Domain = subflowCorrelation.SubFlowDomain,
            Workflow = subflowCorrelation.SubFlowName,
            Version = subflowCorrelation.SubFlowVersion,
            Instance = subflowCorrelation.SubFlowInstanceId.ToString(),
            Headers = input.Headers,
            QueryParams = input.RouteValues
        };

        var subflowStateResult = await instanceQueryGateway.GetFunctionWithStateAsync(
            subflowStateInput,
            cancellationToken);

        if (!subflowStateResult.IsSuccess)
        {
            return Result<RetryInstanceOutput>.Fail(subflowStateResult.Error);
        }

        var subflowState = subflowStateResult.Value!;

        // SubFlow not Faulted → Error (both instance and subflow are not faulted)
        if (subflowState.Status == null || !subflowState.Status.Equals(InstanceStatus.Faulted))
        {
            return Result<RetryInstanceOutput>.Fail(Error.Validation(
                WorkflowErrorCodes.InstanceNotFaulted,
                $"Instance {input.Instance} is not in faulted state. Current status: {parentInstance.Status.Code}",
                input.Instance));
        }

        // Create retry input for SubFlow
        var subflowInput = new RetryInstanceInput
        {
            Domain = subflowCorrelation.SubFlowDomain,
            Workflow = subflowCorrelation.SubFlowName,
            Version = subflowCorrelation.SubFlowVersion,
            Instance = subflowCorrelation.SubFlowInstanceId.ToString(),
            Sync = input.Sync,
            Data = input.Data,
            Headers = input.Headers,
            RouteValues = input.RouteValues
        };

        logger.InstanceRetryRequested(subflowCorrelation.SubFlowInstanceId.ToString(), subflowCorrelation.SubFlowName);

        // Use gateway for retry - routes to local or remote based on domain
        return await instanceRetryGateway.RetryAsync(subflowInput, cancellationToken);
    }

    /// <summary>
    /// Retries a faulted instance by re-executing its incomplete transition.
    /// </summary>
    private async Task<Result<RetryInstanceOutput>> RetryFaultedInstanceAsync(
        Instance instance,
        RetryInstanceInput input,
        CancellationToken cancellationToken)
    {
        // Railway chain: Load workflow -> Find incomplete transition -> Unfault -> Execute
        return await LoadWorkflowAsync(input, instance, cancellationToken)
            .BindAsync(data => FindIncompleteTransitionAsync(data.Instance, data.Workflow, cancellationToken))
            .BindAsync(data => UnfaultAndPersistAsync(data, cancellationToken))
            .BindAsync(data => ExecuteRetryAsync(data, input, cancellationToken));
    }

    /// <summary>
    /// Load the workflow definition.
    /// </summary>
    private async Task<Result<(Instance Instance, WorkflowDefinition Workflow)>> LoadWorkflowAsync(
        RetryInstanceInput input,
        Instance instance,
        CancellationToken cancellationToken)
    {
        var workflowResult = await componentCacheStore.GetFlowAsync(
            input.Domain,
            input.Workflow,
            input.Version,
            cancellationToken);

        return workflowResult.Map(workflow => (instance, workflow));
    }

    /// <summary>
    /// Find the incomplete transition (faulted transition) for the instance.
    /// </summary>
    private async Task<Result<(Instance Instance, WorkflowDefinition Workflow, InstanceTransition Transition)>> 
        FindIncompleteTransitionAsync(
            Instance instance,
            WorkflowDefinition workflow,
            CancellationToken cancellationToken)
    {
        var transition = await instanceTransitionRepository.GetLatestIncompleteAsync(
            instance.Id,
            cancellationToken);

        if (transition == null)
        {
            return Result<(Instance, WorkflowDefinition, InstanceTransition)>.Fail(
                Error.Validation(
                    WorkflowErrorCodes.NoIncompleteTransitionFound,
                    $"No incomplete transition found for instance {instance.Id}",
                    instance.Id.ToString()));
        }

        return Result<(Instance, WorkflowDefinition, InstanceTransition)>.Ok((instance, workflow, transition));
    }

    /// <summary>
    /// Unfault the instance and persist the change.
    /// </summary>
    private async Task<Result<(Instance Instance, WorkflowDefinition Workflow, InstanceTransition Transition)>>
        UnfaultAndPersistAsync(
            (Instance Instance, WorkflowDefinition Workflow, InstanceTransition Transition) data,
            CancellationToken cancellationToken)
    {
        await using var uow = await UnitOfWorkManager.BeginRequiresNew(cancellationToken);

        var unfaulted = data.Instance.Unfault();
        if (!unfaulted)
        {
            return Result<(Instance, WorkflowDefinition, InstanceTransition)>.Fail(
                Error.Validation(
                    WorkflowErrorCodes.InstanceNotFaulted,
                    $"Instance {data.Instance.Id} could not be unfaulted",
                    data.Instance.Id.ToString()));
        }

        logger.InstanceUnfaulted(data.Instance.Id);

        await instanceRepository.UpdateAsync(data.Instance, true, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        return Result<(Instance, WorkflowDefinition, InstanceTransition)>.Ok(data);
    }

    /// <summary>
    /// Execute the transition retry using the workflow execution service.
    /// </summary>
    private async Task<Result<RetryInstanceOutput>> ExecuteRetryAsync(
        (Instance Instance, WorkflowDefinition Workflow, InstanceTransition Transition) data,
        RetryInstanceInput input,
        CancellationToken cancellationToken)
    {
        var context = new WorkflowExecutionContext
        {
            Domain = input.Domain,
            InstanceId = data.Instance.Id.ToString(),
            WorkflowKey = input.Workflow,
            WorkflowVersion = input.Version ?? data.Workflow.Version,
            TransitionKey = data.Transition.TransitionId,
            TriggerType = TriggerType.Manual, // Retry is always manual
            Mode = input.Sync ? ExecMode.Sync : ExecMode.Async,
            Actor = Shared.ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Headers = input.Headers,
            RouteValues = input.RouteValues,
            IsReentry = true, // Retry is a re-entry scenario
            Data = input.Data != null 
                ? new TransitionDataInfo(input.Data.Key, input.Data.Attributes) { Tags = input.Data.Tags } 
                : null,
            Execution = new ExecutionInfo
            {
                ExecutionChainId = Guid.NewGuid().ToString("N"),
                ChainDepth = 0
            },
            Retry = new RetryInfo
            {
                TransitionId = data.Transition.Id
            }
        };

        var result = await workflowExecutionService.ExecuteTransitionAsync(context, cancellationToken);

        if (!result.IsSuccess)
            return Result<RetryInstanceOutput>.Fail(result.Error);

        // Reload instance to get current status
        var refreshedInstance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
            data.Instance.Id.ToString(),
            cancellationToken);

        var currentStatus = refreshedInstance?.Status ?? result.Value!.Status;

        if (currentStatus?.Equals(InstanceStatus.Faulted) == true)
        {
            logger.InstanceRetryFailed(data.Instance.Id, "Instance faulted again after retry");
        }
        else
        {
            logger.InstanceRetrySucceeded(data.Instance.Id);
        }

        return Result<RetryInstanceOutput>.Ok(new RetryInstanceOutput
        {
            Id = result.Value!.Id,
            Status = currentStatus,
            RetriedTransitionId = data.Transition.Id
        });
    }
}
