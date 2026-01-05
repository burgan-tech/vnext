using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Shared;

/// <summary>
/// Base class for models that contain href properties
/// </summary>
public class HrefBase
{
    /// <summary>
    /// The href URL for the resource
    /// </summary>
    public string Href { get; set; } 
}

/// <summary>
/// Transition item with href link
/// </summary>
public sealed class TransitionItem : HrefBase
{
    /// <summary>
    /// Transition name
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Schema href link
    /// </summary>
    public HrefBase? Schema {get; set; }
}

/// <summary>
/// Data href link
/// </summary>
public sealed class DataHref : HrefBase
{
}

/// <summary>
/// View href link with load data flag
/// </summary>
public sealed class ViewHref : HrefBase
{
    /// <summary>
    /// Whether to load data
    /// </summary>
    public bool LoadData { get; set; } = true;
}

/// <summary>
/// Active correlation with href link and additional properties
/// </summary>
public sealed class ActiveCorrelationHref : HrefBase
{
    /// <summary>
    /// Correlation ID
    /// </summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Parent state
    /// </summary>
    public string ParentState { get; set; } = string.Empty;

    /// <summary>
    /// SubFlow instance ID
    /// </summary>
    public Guid SubFlowInstanceId { get; set; }

    /// <summary>
    /// SubFlow type
    /// </summary>
    public SubFlowType SubFlowType { get; set; }

    /// <summary>
    /// SubFlow domain
    /// </summary>
    public string SubFlowDomain { get; set; } = string.Empty;

    /// <summary>
    /// SubFlow name
    /// </summary>
    public string SubFlowName { get; set; } = string.Empty;

    /// <summary>
    /// SubFlow version
    /// </summary>
    public string? SubFlowVersion { get; set; }

    /// <summary>
    /// Whether the correlation is completed
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// Status of the correlation (optional)
    /// </summary>
    public InstanceStatus? Status { get; set; }

    /// <summary>
    /// Current state of the correlation (optional)
    /// </summary>
    public string? CurrentState { get; set; }
}