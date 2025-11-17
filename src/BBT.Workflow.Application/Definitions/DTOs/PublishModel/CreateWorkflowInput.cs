using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Definitions;

public sealed class CreateWorkflowInput
{
    /// <summary>
    /// Determines the course of the flow.
    /// </summary>
    [Required]
    [StringLength(WorkflowConstants.MaxTypeLength)]
    [AllowedValues("C", "F", "S", "P",
        ErrorMessage = "The value must be one of the following: C (Core), F (Flow), S (SubFlow), P (SubProcess).")]
    public string Type { get; set; }

    /// <summary>
    /// When the workflow starts, a timer counts down.
    /// If the workflow is not completed within this time,
    /// it is automatically pulled to the targeted status.
    /// </summary>
    public CreateWorkflowTimeoutInput? Timeout { get; set; }

    /// <summary>
    /// It is a content set with multiple language options for the content to be displayed to the user.
    /// </summary>
    public List<CreateLanguageLabelInput> Labels { get; set; } = [];

    /// <summary>
    /// These are function definitions that will be distributed with the flow.
    /// In general, BFF and calculation methods are defined as functions.
    /// </summary>
    public List<ReferenceInput>? Functions { get; set; } = [];

    /// <summary>
    /// Definitions that include transition and interface components
    /// that can be used in common in all flows such as adding documents and adding notes.
    /// </summary>
    public List<ReferenceInput>? Features { get; set; } = [];

    /// <summary>
    /// It is used for common transition definitions such as Cancel in the flow.
    /// It is to prevent redefinition in each state that passes.
    /// </summary>
    public List<CreateTransitionInput>? SharedTransitions { get; set; } = [];

    /// <summary>
    /// Specifies additional functions to be run when a recording of a flow sample is requested.
    /// It is generally used to enrich the recording.
    /// </summary>
    public List<ReferenceInput>? Extensions { get; set; } = [];

    /// <summary>
    /// All flows are started with a fixed transition named start.
    /// There is no interface component in the transition but it can receive a dataset.
    /// It contains the basic definitions related to this transition.
    /// </summary>
    [Required]
    public CreateTransitionInput StartTransition { get; set; }

    /// <summary>
    /// It is in the possible statuses found in the flow.
    /// </summary>
    [Required]
    public List<CreateStateInput> States { get; set; } = [];

    /// <summary>
    /// Master schema reference for instance data validation.
    /// If provided, instance data will be validated against this schema.
    /// </summary>
    public ReferenceInput? Schema { get; set; }

    /// <summary>
    /// Instance data validation configuration.
    /// Determines when and how instance data should be validated against the master schema.
    /// </summary>
    public CreateInstanceDataValidationConfigInput? InstanceDataValidation { get; set; }
}

public class CreateWorkflowTimeoutInput
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
    /// The name of the status to which the flow will be drawn.
    /// This status is expected to be of the finish State type.
    /// </summary>
    [Required]
    [StringLength(StateConstants.MaxKeyLength)]
    [RegularExpression(@"^[a-zA-Z0-9\-]+$",
        ErrorMessage = "The Target field can only contain alphanumeric characters (A-Z, a-z, 0-9) and hyphens (-).")]
    public string Target { get; set; }

    [Required]
    [StringLength(TransitionConstants.MaxVersionStrategyLength)]
    [AllowedValues("Minor", "Major")]
    public string VersionStrategy { get; set; }

    public TimerConfigInput Timer { get; set; }
}

public class TimerConfigInput
{
    [Required] public string Reset { get; set; }

    /// <summary>
    /// Duration ISO 8601
    /// </summary>
    [Required]
    [StringLength(WorkflowConstants.MaxDurationLength)]
    [RegularExpression(@"^P(?=\d|T\d)(\d+Y)?(\d+M)?(\d+D)?(T(\d+H)?(\d+M)?(\d+S)?)?$",
        ErrorMessage = "Invalid duration format. Must be ISO 8601 compliant.")]
    public string Duration { get; set; }
}

/// <summary>
/// Instance data validation configuration input
/// </summary>
public class CreateInstanceDataValidationConfigInput
{
    /// <summary>
    /// When to validate instance data against the master schema.
    /// Possible values: 0 (None), 1 (PostExecution), 2 (OnDataChange)
    /// </summary>
    [Required]
    [Range(0, 2, ErrorMessage = "ValidationMode must be 0 (None), 1 (PostExecution), or 2 (OnDataChange)")]
    public int ValidationMode { get; set; } = 1; // Default: PostExecution
}