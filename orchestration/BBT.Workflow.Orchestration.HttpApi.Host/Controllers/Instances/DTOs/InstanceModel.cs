

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

    /// <summary>
    /// Activation-episode context carried in the internal request body. These values deliberately
    /// do not travel as trace headers: the child request keeps its own server-span anchor while its
    /// time-to-available measurement starts with the parent episode.
    /// </summary>
    public DateTimeOffset? EpisodeStartedAt { get; set; }
    public string? EpisodeTrigger { get; set; }
    public string? EpisodeTransitionKey { get; set; }
    public string? EpisodeTraceRoot { get; set; }
}
