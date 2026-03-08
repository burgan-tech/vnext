using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Definitions;

public abstract class PublishBaseInput
{
    /// <summary>
    /// If present, it is the more readable key value of the record.
    /// </summary>
    [Required]
    [StringLength(WorkflowConstants.MaxKeyLength)]
    [RegularExpression(@"^[a-zA-Z0-9\-]+$",
        ErrorMessage = "The Key field can only contain alphanumeric characters (A-Z, a-z, 0-9) and hyphens (-).")]
    public string Key { get; set; }
    
    [Required]
    [StringLength(WorkflowConstants.MaxKeyLength)]
    [RegularExpression(@"^[a-zA-Z0-9\-]+$",
        ErrorMessage = "The Key field can only contain alphanumeric characters (A-Z, a-z, 0-9) and hyphens (-).")]
    public string Flow { get; set; }

    /// <summary>
    /// This is information about the domain on which the stream where the record is located.
    /// </summary>
    [Required]
    [StringLength(WorkflowConstants.MaxDomainLength)]
    [RegularExpression(@"^[a-zA-Z\-]+$",
        ErrorMessage = "The Domain field can only contain alphabetic characters (A-Z) and hyphens (-).")]
    public string Domain { get; set; }

    /// <summary>
    /// This is the version information at the time the record is assigned.
    /// </summary>
    [Required]
    [StringLength(WorkflowConstants.MaxVersionLength)]
    public string Version { get; set; }
    
    [Required]
    [StringLength(WorkflowConstants.MaxVersionLength)]
    public string FlowVersion { get; set; }
    
    public List<string> Tags { get; set; } = [];
}