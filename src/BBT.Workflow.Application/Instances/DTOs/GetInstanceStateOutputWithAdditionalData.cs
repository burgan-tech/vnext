namespace BBT.Workflow.Instances;

/// <summary>
/// Extended output for instance state with additional custom data
/// Inherits from GetInstanceStateOutput and adds AdditionalData property
/// </summary>
public sealed class GetInstanceStateOutputWithAdditionalData : GetInstanceStateOutput
{
    /// <summary>
    /// Additional custom data from task configuration
    /// </summary>
    public object? AdditionalData { get; set; }
}

