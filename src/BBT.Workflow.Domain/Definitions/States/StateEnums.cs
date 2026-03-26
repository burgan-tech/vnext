namespace BBT.Workflow.Definitions;

/// <summary>
/// Main state types
/// </summary>
public enum StateType
{
    /// <summary>
    /// Starting state of the workflow
    /// </summary>
    Initial = 1,

    /// <summary>
    /// Middle states where work is being done
    /// </summary>
    Intermediate = 2,

    /// <summary>
    /// End states of the workflow
    /// </summary>
    Finish = 3,

    /// <summary>
    /// State that executes another workflow
    /// </summary>
    SubFlow = 4,
    /// <summary>
    /// State that has wizard view
    /// </summary>
    Wizard = 5
}

/// <summary>
/// Subtypes
/// </summary>
public enum StateSubType
{
    /// <summary>
    /// No specific subtype
    /// </summary>
    None = 0,

    /// <summary>
    /// Successful completion
    /// </summary>
    Success = 1,

    /// <summary>
    /// Error condition
    /// </summary>
    Error = 2,

    /// <summary>
    /// Manually terminated
    /// </summary>
    Terminated = 3,

    /// <summary>
    /// Temporarily suspended
    /// </summary>
    Suspended = 4,

    /// <summary>
    /// State is busy processing (e.g., long-running operation, async task)
    /// When instance enters Busy subtype, instance status is automatically set to Busy
    /// </summary>
    Busy = 5,

    /// <summary>
    /// State requires human intervention (e.g., approval, manual review)
    /// Enables efficient querying of instances waiting for human action
    /// </summary>
    Human = 6,
    
    Cancelled = 7,
    
    Timeout = 8
}