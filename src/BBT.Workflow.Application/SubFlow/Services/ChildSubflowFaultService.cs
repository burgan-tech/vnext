using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Default <see cref="IChildSubflowFaultService"/>. Mirrors the load + terminal-guard + fault
/// logic that previously lived inline in the Inbox <c>ChildSubflowFaultRequestedEventHandler</c>,
/// relocated to Orchestration so the Inbox stays a thin forwarder. The unit of work and current
/// schema are supplied by the calling endpoint's host middleware + the X-Workflow header.
/// </summary>
public sealed class ChildSubflowFaultService(
    IInstanceRepository instanceRepository,
    ILogger<ChildSubflowFaultService> logger) : IChildSubflowFaultService
{
    /// <inheritdoc />
    public async Task<Result> FaultChildAsync(
        Guid instanceId,
        string domain,
        string flow,
        Guid parentInstanceId,
        CancellationToken cancellationToken = default)
    {
        var childInstance = await instanceRepository.FindAsync(instanceId, true, cancellationToken);

        if (childInstance is null)
        {
            logger.InstanceNotFound(instanceId, flow);
            return Result.Ok();
        }

        // Idempotency: skip if child is already in a terminal state.
        if (childInstance.Status.Equals(InstanceStatus.Faulted) ||
            childInstance.Status.Equals(InstanceStatus.Completed))
        {
            return Result.Ok();
        }

        childInstance.Fault(domain);
        await instanceRepository.UpdateAsync(childInstance, true, cancellationToken);

        logger.ChildSubflowFaultApplied(instanceId, parentInstanceId);
        return Result.Ok();
    }
}
