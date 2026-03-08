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
    /// View href for this transition. When HasView is true, the view endpoint returns meaningful content when called with this transition key.
    /// </summary>
    public ViewHref? View { get; set; }

    /// <summary>
    /// Schema href for this transition. When HasSchema is true, the schema endpoint returns meaningful content for this transition key.
    /// </summary>
    public SchemaHref? Schema { get; set; }
}

/// <summary>
/// Data href link
/// </summary>
public sealed class DataHref : HrefBase
{
}

/// <summary>
/// Schema href link with has-schema flag. When true, the schema endpoint returns meaningful content for the transition.
/// </summary>
public sealed class SchemaHref : HrefBase
{
    /// <summary>
    /// Whether this transition has a schema reference. When true, the schema endpoint returns meaningful content.
    /// </summary>
    public bool HasSchema { get; set; }
}

/// <summary>
/// View href link with load data flag
/// </summary>
public sealed class ViewHref : HrefBase
{
    /// <summary>
    /// Whether the current state has a view definition (state view or wizard single-transition view). When true, the view endpoint returns meaningful content.
    /// </summary>
    public bool HasView { get; set; }

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