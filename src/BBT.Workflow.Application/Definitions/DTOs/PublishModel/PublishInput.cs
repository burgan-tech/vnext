using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace BBT.Workflow.Definitions;

public sealed class PublishInput : PublishBaseInput
{
    [Required] public JsonElement Attributes { get; set; }
    public List<PublishDataInput>? Data { get; set; }
}

public sealed class PublishDataInput
{
    /// <summary>
    /// If present, it is the more readable key value of the record.
    /// </summary>
    [Required]
    [StringLength(WorkflowConstants.MaxKeyLength)]
    [RegularExpression(@"^[a-zA-Z0-9\-]+$",
        ErrorMessage = "The Key field can only contain alphanumeric characters (A-Z, a-z, 0-9) and hyphens (-).")]
    public string Key { get; set; }
    
    /// <summary>
    /// This is the version information at the time the record is assigned.
    /// </summary>
    [Required]
    [StringLength(WorkflowConstants.MaxVersionLength)]
    public string Version { get; set; }
    
    public List<string> Tags { get; set; } = [];

    [Required]
    public JsonElement Attributes { get; set; }
}