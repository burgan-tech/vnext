using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Output for retrieving instance state with combined information
/// </summary>
public class GetInstanceStateOutput
{
    /// <summary>
    /// Data href link with optional extensions
    /// </summary>
    public DataHref Data { get; set; } = new();

    /// <summary>
    /// View href link
    /// </summary>
    public ViewHref View { get; set; } = new();

    /// <summary>
    /// Current state of the instance
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Instance status
    /// </summary>
    public InstanceStatus? Status { get; set; }

    /// <summary>
    /// Active correlations with href links
    /// </summary>
    public List<ActiveCorrelationHref> ActiveCorrelations { get; set; } = [];

    /// <summary>
    /// Available transition items with href links
    /// </summary>
    public List<TransitionItem> Transitions { get; set; } = [];

    /// <summary>
    /// ETag from the latest instance data
    /// </summary>
    public string ETag { get; set; } = string.Empty;
}
