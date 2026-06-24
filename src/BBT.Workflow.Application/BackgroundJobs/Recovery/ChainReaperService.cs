using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.BackgroundJobs.Recovery;

/// <inheritdoc cref="IChainReaperService" />
/// <remarks>
/// Operates within the CURRENT schema only — one sweep == one flow schema. The hosting
/// <c>ChainReaperHostedService</c> enumerates all flow schemas from <c>sys_flows</c> and invokes
/// this once per schema with <c>IcurrentSchema.Change(flowKey)</c> already established, so all
/// repository queries here resolve to that flow's schema. Re-enqueue of a recoverable
/// continuation is intentionally NOT attempted here (conservative): a stuck chain with no live
/// job is faulted.
/// </remarks>
public sealed class ChainReaperService(
    IUnitOfWorkManager uowManager,
    IInstanceRepository instanceRepository,
    IInstanceJobRepository jobRepository,
    IRuntimeInfoProvider runtimeInfoProvider,
    IOptions<WorkflowExecutionOptions> options,
    ILogger<ChainReaperService> logger) : IChainReaperService
{
    private const int MaxPerSweep = 100;

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        // Staleness threshold must comfortably exceed the job execution budget so the reaper
        // never races a live job (default 3x the job timeout).
        var thresholdSeconds = Math.Max(60, options.Value.TransitionJobTimeoutSeconds * 3);
        var olderThan = DateTime.UtcNow.AddSeconds(-thresholdSeconds);

        var faulted = 0;
        var skippedActive = 0;

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        var candidates = await instanceRepository.GetStuckBusyChainsAsync(olderThan, MaxPerSweep, cancellationToken);

        foreach (var instance in candidates)
        {
            // A live/pending job means the chain is still progressing — leave it alone.
            var activeJobs = await jobRepository.GetListActiveAsync(instance.Id, cancellationToken);
            if (activeJobs.Count > 0)
            {
                skippedActive++;
                continue;
            }

            var incident = InstanceIncidentFactory.Create(
                state: instance.GetCurrentState,
                transition: string.Empty,
                taskKey: null,
                message: "Auto-chain stalled: instance Busy with no live job past the heartbeat threshold",
                errorCode: "CHAIN_STALLED",
                errorLayer: "Reaper",
                boundaryAction: "Abort",
                boundaryLevel: "Global");

            logger.ChainReaperFaultedInstance(instance.Id, instance.ChainToken, instance.ChainHeartbeatAt);

            instance.AddIncident(incident);
            instance.Fault(runtimeInfoProvider.Domain);
            await instanceRepository.UpdateAsync(instance, true, cancellationToken);
            faulted++;
        }

        await uow.CommitAsync(cancellationToken);

        logger.ChainReaperSweepCompleted(faulted, skippedActive);
        return faulted;
    }
}
