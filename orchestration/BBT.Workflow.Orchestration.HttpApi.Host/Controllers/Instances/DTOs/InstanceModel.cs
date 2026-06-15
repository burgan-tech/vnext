

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Instances;
public class CreateInstanceDto
{
    [StringLength(InstanceConstants.MaxKeyLength)]
    public string? Key { get; set; }
    public string[]? Tags { get; set; }
    public JsonElement? Attributes { get; set; }

    /// <summary>
    /// Optional stage label for the instance (max 120 characters).
    /// </summary>
    [StringLength(InstanceConstants.MaxStageLength)]
    public string? Stage { get; set; }
}

public class CreateSubInstanceDto : CreateInstanceDto
{
    public Guid? Id  { get; set; }
    public string? Callback { get; set; }
    public Dictionary<string, object?> ExtraProperties { get; set; }
}