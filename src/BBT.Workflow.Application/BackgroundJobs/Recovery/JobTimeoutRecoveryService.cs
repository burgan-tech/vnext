using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.BackgroundJobs.Recovery;

/// <inheritdoc cref="IJobTimeoutRecoveryService" />
public sealed class JobTimeoutRecoveryService(
    IUnitOfWorkManager uowManager,
    IInstanceRepository instanceRepository,
    IInstanceTransitionRepository transitionRepository,
    IOptions<WorkflowExecutionOptions> options,
    ILogger<JobTimeoutRecoveryService> logger) : IJobTimeoutRecoveryService
{
    public Task FaultInstanceAsync(TransitionJobPayload args, CancellationToken cancellationToken)
        => FaultInstanceAsync(
            args,
            $"Job execution timed out or was cancelled after {options.Value.TransitionJobTimeoutSeconds}s",
            "JOB_EXECUTION_TIMEOUT",
            cancellationToken);

    public async Task FaultInstanceAsync(
        TransitionJobPayload args,
        string incidentMessage,
        string incidentErrorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

            var instance = await instanceRepository.FindAsync(args.InstanceId, true, cancellationToken);

            if (instance == null)
            {
                logger.InstanceNotFound(args.InstanceId, args.Workflow);
                return;
            }

            if (!instance.Status.Equals(InstanceStatus.Busy))
            {
                logger.LogDebug(
                    "Recovery skipped: instance {InstanceId} is not in Busy status (current: {Status})",
                    args.InstanceId, instance.Status);
                return;
            }

            var incident = InstanceIncidentFactory.Create(
                state: instance.GetCurrentState,
                transition: args.TransitionKey,
                taskKey: null,
                message: incidentMessage,
                errorCode: incidentErrorCode,
                errorLayer: "Job",
                boundaryAction: "Abort",
                boundaryLevel: "Job");

            instance.AddIncident(incident);
            instance.Fault(args.Domain);

            var openTransition = await transitionRepository.GetLatestIncompleteAsync(
                args.InstanceId, cancellationToken);
            if (openTransition != null)
            {
                openTransition.Failed();
                await transitionRepository.UpdateCompletedAsync(openTransition, cancellationToken);
            }

            await instanceRepository.UpdateAsync(instance, true, cancellationToken);
            await uow.CommitAsync(cancellationToken);

            logger.LogError(
                "Instance {InstanceId} faulted after job execution timeout. " +
                "Transition: {TransitionKey}, State: {State}",
                args.InstanceId, args.TransitionKey, instance.GetCurrentState);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Recovery failed for instance {InstanceId}. Instance may remain in Busy status",
                args.InstanceId);
        }
    }
}
