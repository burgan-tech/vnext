using System.Diagnostics;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Cancellation;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit.Relay;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.BackgroundJobs.Recovery;

/// <inheritdoc cref="IJobTimeoutRecoveryService" />
public sealed class JobTimeoutRecoveryService(
    IUnitOfWorkManager uowManager,
    IInstanceRepository instanceRepository,
    IInstanceTransitionRepository transitionRepository,
    IOptions<WorkflowExecutionOptions> options,
    ILogger<JobTimeoutRecoveryService> logger,
    ISubItemEventDataResolver? subItemDataResolver = null,
    IPostCommitRelayDispatcher? relayDispatcher = null) : IJobTimeoutRecoveryService
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
        // Set only once the fault has committed, so a failed recovery relays nothing.
        IReadOnlyList<DomainEventEnvelope> faultEvents = [];

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
            instance.Fault(args.Domain,
                subItemData: await subItemDataResolver.ResolveOrStoredAsync(instance, cancellationToken));

            // Snapshot BEFORE the save: the SaveChanges sink drains and clears the aggregate's events.
            var raisedEvents = instance.GetDomainEvents().ToList();

            var openTransition = await transitionRepository.GetLatestIncompleteAsync(
                args.InstanceId, cancellationToken);
            if (openTransition != null)
            {
                openTransition.Failed();
                await transitionRepository.UpdateCompletedAsync(openTransition, cancellationToken);
            }

            await instanceRepository.UpdateAsync(instance, true, cancellationToken);
            ActivityContext commitContext;
            using (var commitActivity = PipelineStepActivityHelper.StartTransitionActivity(
                       "Uow.Commit", args.TransitionKey))
            {
                await uow.CommitAsync(cancellationToken);
                commitContext = commitActivity?.Context ?? default;
            }

            faultEvents = raisedEvents;

            // The job's episode is still ambient here (TransitionJobHandler runs recovery inside its
            // lane scope): a job that timed out rests the instance Faulted, and that is the client's
            // rest point.
            //
            // Exactly ONE emit per episode. This block stood here three times — same outcome, same
            // instance, same episode — so a recovered timeout produced three Instance.Activation
            // spans, two of them detached from the commit that made the rest point durable, and
            // three samples in workflow_activation_duration_ms for one activation. The surviving
            // call is the one carrying settlingCommit; the other two carried nothing extra.
            ActivationActivity.Emit(
                PipelineStepActivityHelper.ActivitySource,
                TelemetryConstants.ActivationOutcomes.Faulted,
                args.InstanceId,
                args.Domain,
                args.Workflow,
                args.TransitionKey,
                instance.GetCurrentState,
                settlingCommit: commitContext);

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

        await RelayFaultEventsAsync(faultEvents);
    }

    /// <summary>
    /// Relays the events the committed fault raised, outside its unit of work. A timed-out job that
    /// is a sub item raised its parent's <c>InstanceSubFaultedEvent</c> here, in a unit of work no
    /// runner relays from; without the relay a parent in another domain never hears of it, because
    /// the child domain's Inbox drops the event (vnext#1256). Bounded by the compensation budget
    /// rather than the recovery's own token, which is deliberately <c>None</c>; the dispatcher
    /// never throws, and the outbox row stays the durable record.
    /// </summary>
    private async Task RelayFaultEventsAsync(IReadOnlyList<DomainEventEnvelope> faultEvents)
    {
        if (relayDispatcher is null || faultEvents.Count == 0)
            return;

        using var compensation = CompensationCancellation.Create();
        await relayDispatcher.RelayAsync(faultEvents, compensation.Token);
    }
}
