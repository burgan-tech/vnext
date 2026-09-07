using BBT.Aether.Application.Services;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Caching;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;
using BBT.Workflow.Authorization;

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
    IInstanceIncidentRepository instanceIncidentRepository,
    IInstanceTransitionRepository instanceTransitionRepository,
    IInstanceQueryGateway instanceQueryGateway,
    IInstanceRetryGateway instanceRetryGateway,
    IComponentCacheStore componentCacheStore,
    IWorkflowExecutionService workflowExecutionService,
    ICallerRoleResolver callerRoleResolver,
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

        // Step 1: Load instance with details (including correlations), NO-TRACKING.
        //
        // Read-only on purpose. Every write on this path is performed by an inner unit of work: the
        // unfault is a set-based CAS, and the retried transition runs in the pipeline's own scope.
        // A tracked load here made the ambient request unit of work hold a copy of the aggregate
        // that Unfault() had set to Active — and its commit, at the very end of the request, wrote
        // that stale Active over the Faulted the re-failed retry had just persisted. The instance
        // was then left Active, parked, with an open incident and no way to retry it again.
        var instanceResult = await instanceRepository.GetResultAsReadOnlyAsync(input.Instance, cancellationToken);
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
        var callerRoles = await callerRoleResolver.ResolveRolesAsync(input.Headers, cancellationToken);
        if (!callerRoles.IsSuccess)
            return Result<RetryInstanceOutput>.Fail(callerRoles.Error);

        // Query SubFlow state via gateway (supports cross-domain)
        var subflowStateInput = new GetFunctionWithInstanceInput
        {
            Domain = subflowCorrelation.SubFlowDomain,
            Workflow = subflowCorrelation.SubFlowName,
            Version = subflowCorrelation.SubFlowVersion,
            Instance = subflowCorrelation.SubFlowInstanceId.ToString(),
            Headers = input.Headers,
            QueryParams = input.RouteValues,
            Role = ICallerRoleResolver.SingleRoleOf(callerRoles.Value),
            Roles = callerRoles.Value
        };

        // Retry descends rarely, but leaving one descent unspanned would cost more than it saves:
        // "this trace contains no Subflow.Descend" has to mean "nothing descended", or it means
        // nothing at all.
        using var descent = InstanceReadActivityHelper.StartDescendScope(
            runtimeInfoProvider,
            subflowCorrelation.SubFlowDomain,
            subflowCorrelation.SubFlowName,
            subflowCorrelation.SubFlowInstanceId.ToString(),
            subflowCorrelation.ParentInstanceId.ToString(),
            TelemetryConstants.DescentFunctions.State);

        var subflowStateResult = await instanceQueryGateway.GetFunctionWithStateAsync(
            subflowStateInput,
            cancellationToken);

        if (!subflowStateResult.Result.IsSuccess)
        {
            return Result<RetryInstanceOutput>.Fail(subflowStateResult.Result.Error);
        }

        var subflowState = subflowStateResult.Result.Value!;

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
            Instance = subflowCorrelation.SubFlowInstanceId.ToString(),
            Sync = true,
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
    /// If the instance has an active SubFlow correlation (fault propagated upward from SubFlow),
    /// unfaults the parent and cascades retry to the SubFlow instead.
    /// </summary>
    private async Task<Result<RetryInstanceOutput>> RetryFaultedInstanceAsync(
        Instance instance,
        RetryInstanceInput input,
        CancellationToken cancellationToken)
    {
        // Check if the fault originated from a SubFlow (correlation stays open on fault)
        if (instance.HasActiveSubFlow)
        {
            var subflowCorrelation = instance.Subflow!;

            // Unfault parent first. Must be COMMITTED before the child retry runs: the pipeline
            // rejects an instance whose status is still terminal, and Faulted is terminal.
            await using var uow = UnitOfWorkManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
            // Incidents live in their own table and no load path includes them; the CAS below
            // resolves what is materialized here.
            await instanceRepository.LoadActiveIncidentsAsync(instance, cancellationToken);
            if (!await instanceRepository.TryUnfaultAsync(instance, cancellationToken))
            {
                return Result<RetryInstanceOutput>.Fail(Error.Validation(
                    WorkflowErrorCodes.InstanceNotFaulted,
                    $"Instance {instance.Id} is no longer in faulted state",
                    instance.Id.ToString()));
            }

            var resolvedCount = await instanceIncidentRepository.ResolveAllAsync(
                instance.Id, DateTime.UtcNow, cancellationToken);
            await uow.CommitAsync(cancellationToken);

            logger.InstanceUnfaulted(instance.Id);
            if (resolvedCount > 0)
                logger.IncidentsResolved(instance.Id, resolvedCount);

            // Delegate retry to the SubFlow via existing gateway path
            return await RetrySubFlowAsync(instance, subflowCorrelation, input, cancellationToken);
        }

        // Standard path: Load workflow -> Find incomplete transition -> Unfault -> Execute
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
            instance.FlowVersion,
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
        await using var uow = UnitOfWorkManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        // See RetryFaultedInstanceAsync: the incidents must be loaded before the CAS resolves them.
        await instanceRepository.LoadActiveIncidentsAsync(data.Instance, cancellationToken);

        // Set-based CAS instead of UpdateAsync. Two reasons: the aggregate is detached here (loaded
        // no-tracking in the ambient scope), so Set.Update would rewrite the entire graph — every
        // InstanceData row included — for a four-column flip; and the CAS makes "was still Faulted"
        // part of the write rather than a check that can go stale.
        if (!await instanceRepository.TryUnfaultAsync(data.Instance, cancellationToken))
        {
            return Result<(Instance, WorkflowDefinition, InstanceTransition)>.Fail(
                Error.Validation(
                    WorkflowErrorCodes.InstanceNotFaulted,
                    $"Instance {data.Instance.Id} could not be unfaulted",
                    data.Instance.Id.ToString()));
        }

        // Unconditional and idempotent: the predicate excludes resolved rows, so this closes exactly
        // what was open. The aggregate's own Resolve() calls persist nothing on this path — the rows
        // were read outside a change tracker and are deliberately kept off its navigation.
        var resolvedCount = await instanceIncidentRepository.ResolveAllAsync(
            data.Instance.Id, DateTime.UtcNow, cancellationToken);

        logger.InstanceUnfaulted(data.Instance.Id);

        // Must be committed before ExecuteRetryAsync: the pipeline's instance load rejects a
        // terminal instance, and Faulted counts as terminal.
        await uow.CommitAsync(cancellationToken);

        if (resolvedCount > 0)
            logger.IncidentsResolved(data.Instance.Id, resolvedCount);

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
            WorkflowKey = data.Instance.Flow,
            WorkflowVersion = data.Instance.FlowVersion,
            TransitionKey = data.Transition.TransitionId,
            TriggerType = TriggerType.Manual, // Retry is always manual
            Mode = input.Sync ? ExecMode.Sync : ExecMode.Async,
            CallerMode = input.Sync ? ExecMode.Sync : ExecMode.Async,
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

        // The retry request opened the activation episode; name it so the span reads `retry`.
        using var episode = WorkflowTraceLane.UseEpisode(TelemetryConstants.ActivationTriggers.Retry, data.Transition.TransitionId);

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
