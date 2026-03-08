using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.PostCommit.Handlers;

/// <summary>
/// Post-commit handler for forwarding transitions to active subflow instances.
/// Executes after the distributed lock is released to avoid deadlocks
/// when subflow completion tries to call back to the parent flow.
/// </summary>
public sealed class ForwardToSubflowJobHandler(
    ISubflowForwardingService subflowForwardingService,
    ILogger<ForwardToSubflowJobHandler> logger) : IPostCommitHandler<ForwardToSubflowJob>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        ForwardToSubflowJob job,
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.InstanceId] = context.InstanceId,
            [TelemetryConstants.TagNames.ParentInstanceId] = job.ParentInstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = job.SubflowInstanceId
        }))
        {
            logger.SubFlowForwardStarted(job.TransitionKey, job.SubflowInstanceId, job.ParentInstanceId);

            // Reconstruct TransitionInput from job's primitive values (includes parent instance id header for trace correlation)
            var input = CreateTransitionInput(job);

            // Perform the forward operation (remote call, now outside lock)
            var result = await subflowForwardingService.ForwardTransitionAsync(
                job.SubflowInstanceId,
                job.TransitionKey,
                input,
                cancellationToken,
                job.ParentInstanceId);

            if (result.IsSuccess)
            {
                // Success path - update client response with subflow status
                // Special case: If subflow completed, show parent instance's actual status
                // Otherwise, show subflow's status
                var responseStatus = result.Value!.Status.Equals(InstanceStatus.Completed)
                    ? context.Instance.Status
                    : result.Value.Status;

                context.ClientResponse = new ClientResponse
                {
                    Id = context.InstanceId,
                    Status = responseStatus
                };

                logger.SubFlowForwardSucceeded(job.TransitionKey, job.SubflowInstanceId, job.ParentInstanceId);

                return Result.Ok();
            }

            // Failure path - set error client response
            context.ClientResponse = new ClientResponse
            {
                Id = context.InstanceId,
                Status = context.Instance.Status,
                Error = result.Error
            };

            logger.SubFlowForwardFailed(
                job.SubflowInstanceId,
                job.ParentInstanceId,
                job.TransitionKey,
                result.Error.Code,
                result.Error.Message ?? string.Empty);

            // Propagate error to policy for decision making
            return Result.Fail(result.Error);
        }
    }

    /// <summary>
    /// Creates a TransitionInput from the job's primitive values.
    /// Merges job headers with parent instance id header for trace/log correlation on the remote side.
    /// </summary>
    private static TransitionInput CreateTransitionInput(ForwardToSubflowJob job)
    {
        var headers = new Dictionary<string, string?>(job.Headers)
        {
            [TelemetryConstants.HeaderNames.ParentInstanceId] = job.ParentInstanceId.ToString()
        };

        return new TransitionInput(
            job.SubflowDomain,
            job.SubflowName,
            new TransitionDataInput(job.DataElement)
            {
                Key = job.InstanceKey,
                Tags = job.Tags
            },
            true) // sync = true for forward
        {
            Headers = headers,
            RouteValues = job.RouteValues
        };
    }
}

