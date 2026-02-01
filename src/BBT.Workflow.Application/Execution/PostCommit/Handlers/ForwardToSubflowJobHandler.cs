using BBT.Aether.Results;
using BBT.Workflow.Instances;
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
        logger.LogDebug(
            "Forwarding transition {TransitionKey} to subflow instance {SubflowInstanceId}",
            job.TransitionKey,
            job.SubflowInstanceId);

        // Reconstruct TransitionInput from job's primitive values
        var input = CreateTransitionInput(job);

        // Perform the forward operation (remote call, now outside lock)
        var result = await subflowForwardingService.ForwardTransitionAsync(
            job.SubflowInstanceId,
            job.TransitionKey,
            input,
            cancellationToken);

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

            logger.LogInformation(
                "Successfully forwarded transition {TransitionKey} to subflow instance {SubflowInstanceId}",
                job.TransitionKey,
                job.SubflowInstanceId);
            
            return Result.Ok();
        }

        // Failure path - set error client response
        context.ClientResponse = new ClientResponse
        {
            Id = context.InstanceId,
            Status = context.Instance.Status,
            Error = result.Error
        };

        logger.LogWarning(
            "Forward to subflow instance {SubflowInstanceId} failed for transition {TransitionKey}: {ErrorCode} - {ErrorMessage}",
            job.SubflowInstanceId,
            job.TransitionKey,
            result.Error.Code,
            result.Error.Message);

        // Propagate error to policy for decision making
        return Result.Fail(result.Error);
    }

    /// <summary>
    /// Creates a TransitionInput from the job's primitive values.
    /// </summary>
    private static TransitionInput CreateTransitionInput(ForwardToSubflowJob job)
    {
        return new TransitionInput(
            job.SubflowDomain,
            job.SubflowName,
            job.SubflowVersion,
            new TransitionDataInput(job.DataElement)
            {
                Key = job.InstanceKey,
                Tags = job.Tags
            },
            true // sync = true for forward
        )
        {
            Headers = job.Headers,
            RouteValues = job.RouteValues
        };
    }
}

