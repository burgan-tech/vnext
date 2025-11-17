namespace BBT.Workflow;

/// <summary>
/// Defines workflow-specific error codes and prefixes for consistent error categorization.
/// Error codes follow the pattern: prefix:code
/// Prefixes: App (Application), Instance, Transition, Execution, Validation, Task
/// </summary>
public static class WorkflowErrorCodes
{
    public const string ErrorUri = "https://errors.vnext.io";
    
    #region Application Errors (900xxx)

    public const string Locked = "App:900001";
    public const string ValidationErrors = "App:900002";

    #endregion
    
    #region Instance Errors (100xxx)
    
    public const string NotFoundDomain = "Instance:100001";
    public const string ConflictWorkflow = "Instance:100002";
    public const string NotFoundInitialState = "Instance:100003";
    public const string ConfigInvalid = "Instance:100012";
    public const string NotFoundInstanceData = "Instance:100013";
    public const string NotFoundWorkflow = "Instance:100015";
    
    #endregion
    
    #region Transition Errors (100xxx)
    
    public const string NotFoundTransition = "Transition:100004";
    public const string InvalidState = "Transition:100005";
    public const string RuntimeSchemaInvalidState = "Transition:100006";
    public const string TransitionRuleFailed = "Transition:100007";
    public const string TransitionLocked = "Transition:100009";
    public const string UnauthorizedTransition = "Transition:100010";
    public const string AutoTransitionFailed = "Transition:100011";
    public const string AutoTransitionConditionNotMet = "Transition:100014";
    public const string InstanceDataSchemaValidationFailed = "Instance:100016";
    
    #endregion
    
    #region Execution Errors (200xxx)
    
    public const string ExecutionStepFailed = "Execution:200002";
    
    #endregion
    
    #region Task Errors (400xxx)
    
    public const string TaskContextCreation = "Task:400001";
    public const string TaskExecution = "Task:400002";
    
    #endregion
}