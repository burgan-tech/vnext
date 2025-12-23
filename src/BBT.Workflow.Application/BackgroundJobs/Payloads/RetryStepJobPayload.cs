namespace BBT.Workflow.BackgroundJobs.Payloads;

/// <summary>
/// Represents the payload data for step retry jobs.
/// Contains information needed to resume a pipeline from a specific step after a retry delay.
/// </summary>
public sealed class RetryStepJobPayload
{
    /// <summary>
    /// Gets or sets the unique job name for tracking.
    /// </summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the domain context for the workflow instance.
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unique identifier of the workflow instance.
    /// </summary>
    public Guid InstanceId { get; set; }

    /// <summary>
    /// Gets or sets the name of the workflow definition.
    /// </summary>
    public string WorkflowKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the version of the workflow definition.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the transition key that was being executed.
    /// </summary>
    public string? TransitionKey { get; set; }

    /// <summary>
    /// Gets or sets the order of the step to resume from.
    /// </summary>
    public int StepOrder { get; set; }

    /// <summary>
    /// Gets or sets the name of the step being retried.
    /// </summary>
    public string StepName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current retry attempt number.
    /// </summary>
    public int RetryAttempt { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of retries.
    /// </summary>
    public int MaxRetries { get; set; }

    /// <summary>
    /// Gets or sets the error code from the last failure.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Creates a job name for this retry payload.
    /// </summary>
    public static string CreateJobName(Guid instanceId, int stepOrder, int attempt)
        => $"retry-step-{instanceId:N}-{stepOrder}-{attempt}";
}

