using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Tasks.Factory;

/// <summary>
/// Configuration options for task factory implementations.
/// </summary>
public sealed class TaskFactoryOptions
{
    /// <summary>
    /// Configuration section name for task factory options.
    /// </summary>
    public const string SectionName = "TaskFactory";

    /// <summary>
    /// Gets or sets whether to use object pooling for task instances.
    /// Default is false for development, true for production environments.
    /// </summary>
    public bool UseObjectPooling { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum pool size per task type.
    /// Only applies when UseObjectPooling is true.
    /// </summary>
    [Range(1, 10000, ErrorMessage = "MaxPoolSize must be between 1 and 10000")]
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets the initial pool size per task type.
    /// Only applies when UseObjectPooling is true.
    /// </summary>
    [Range(1, 1000, ErrorMessage = "InitialPoolSize must be between 1 and 1000")]
    public int InitialPoolSize { get; set; } = 10;

    /// <summary>
    /// Gets or sets the task types that should use object pooling.
    /// Only applies when UseObjectPooling is true.
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one task type must be specified for pooling")]
    public string[] PooledTaskTypes { get; set; } = 
    {
        "DaprServiceTask",
        "HttpTask",
        "ScriptTask",
        "ConditionTask",
        "DaprBindingTask",
        "DaprHttpEndpointTask",
        "DaprPubSubTask",
        "DaprServiceTask",
        "HumanTask"
    };
} 