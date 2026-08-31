using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Tasks;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for human workflow tasks that require manual intervention or approval.
/// Currently a placeholder - task execution waits for human interaction.
/// No remote invocation - state changes happen through external human task APIs.
/// </summary>
public sealed class HumanTaskExecutor : TaskExecutorBase<HumanTask>
{
    /// <summary>
    /// Initializes a new instance of HumanTaskExecutor.
    /// </summary>
    public HumanTaskExecutor(ILogger<HumanTaskExecutor> logger)
        : base(logger)
    {
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.Human;

    /// <inheritdoc />
    protected override Task<Result<TaskInvocationResult>> InvokeAsync(
        HumanTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        Logger.LogDebug("HumanTask {TaskKey} execution - task awaiting human interaction", task.Key);

        // Human tasks don't execute immediately - they wait for human interaction
        // Return a pending result that indicates the task is waiting
        var result = TaskInvocationResult.Success(
            data: new
            {
                Status = "Pending",
                Message = "Task awaiting human interaction",
                TaskKey = task.Key
            },
            statusCode: 202, // Accepted - processing is not complete
            taskType: TaskType.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["AwaitingHumanInteraction"] = true
            });

        return Task.FromResult(Result<TaskInvocationResult>.Ok(result));
    }
}

