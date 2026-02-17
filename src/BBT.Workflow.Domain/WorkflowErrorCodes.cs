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

    public const string Dependency = "App:900000";
    public const string Locked = "App:900001";
    public const string ValidationErrors = "App:900002";
    public const string ExecutionStrategyNotSupported = "App:900003";
    public const string TransitionHandlerNotSupported = "App:900004";
    public const string InvalidSchema = "App:900005";
    public const string InvalidWorkflow = "App:900006";
    public const string MigrationFailed = "App:900007";

    #endregion
    
    #region Instance Errors (100xxx)
    
    public const string NotFoundDomain = "Instance:100001";
    public const string ConflictWorkflow = "Instance:100002";
    public const string NotFoundInitialState = "Instance:100003";
    public const string ConfigInvalid = "Instance:100012";
    public const string NotFoundInstanceData = "Instance:100013";
    public const string NotFoundWorkflow = "Instance:100015";
    public const string CancelNotConfiguredForWorkflow = "Instance:100016";
    public const string InstanceNotFound = "Instance:100017";
    public const string InstanceCompleted = "Instance:100018";
    public const string DuplicateInstanceKey = "Instance:100019";
    public const string InstanceCancellationFailed = "Instance:100020";
    public const string ChildSubflowCancellationFailed = "Instance:100021";
    public const string SubflowCompletionFailed = "Instance:100022";
    public const string SubflowStartFailed = "Instance:100023";
    public const string UpdateDataNotConfiguredForWorkflow = "Instance:100024";
    public const string ActiveInstanceAlreadyExists = "Instance:100025";
    public const string ExitNotConfiguredForWorkflow = "Instance:100026";
    public const string InstanceNotFaulted = "Instance:100027";
    public const string NoIncompleteTransitionFound = "Instance:100028";
    
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
    public const string TransitionChainDepthExceeded = "Transition:100017";
    public const string TransitionNotAvailableInCurrentState = "Transition:100020";
    public const string SharedTransitionNotAvailableInState = "Transition:100021";
    public const string StartTransitionNotFromInitialState = "Transition:100022";
    
    #endregion
    
    #region Execution Errors (200xxx)
    
    public const string ExecutionStepFailed = "Execution:200002";
    
    #endregion
    
    #region Task Errors (400xxx)
    
    public const string TaskContextCreation = "Task:400001";
    public const string TaskExecution = "Task:400002";
    public const string TaskPersistenceStrategyNotFound = "Task:400003";
    public const string TaskCreationPersistenceFailed = "Task:400004";
    public const string TaskCompletionPersistenceFailed = "Task:400005";
    public const string TaskHeadersConversionFailed = "Task:400006";
    public const string TaskInputMappingFailed = "Task:400007";
    public const string TaskOutputMappingFailed = "Task:400008";
    public const string UnsupportedTaskType = "Task:400009";
    public const string TaskBindingMappingFailed = "Task:400010";
    public const string TaskExecutionFailed = "Task:400011";
    public const string TaskCoordinationFailed = "Task:400012";
    public const string TaskRemoteInvocationFailed = "Task:400013";
    public const string TaskFactoryCreationFailed = "Task:400014";
    public const string TaskHandlerNotFound = "Task:400015";
    
    #endregion
    
    #region Cache Errors (300xxx)
    
    public const string CacheItemNotFound = "Cache:300001";
    public const string CacheInvalidKey = "Cache:300002";
    public const string CacheTypeNotSupported = "Cache:300003";
    
    #endregion
    
    #region Trigger Errors (500xxx)
    
    public const string TriggerCreateHttpTaskFailed = "Trigger:500001";
    public const string TriggerResolveInstanceFailed = "Trigger:500002";
    public const string TriggerExtractInstanceIdFailed = "Trigger:500003";
    public const string TriggerDirectExecutionFailed = "Trigger:500004";
    public const string TriggerGetInstanceDataFailed = "Trigger:500005";
    public const string TriggerStartExecutionFailed = "Trigger:500006";
    public const string TriggerSubProcessExecutionFailed = "Trigger:500007";
    public const string TriggerInvalidResponseFormat = "Trigger:500008";
    public const string TriggerInvalidResponseStructure = "Trigger:500009";
    
    #endregion
    
    #region Extension Errors (600xxx)
    
    public const string ExtensionExecutionFailed = "Extension:600001";
    
    #endregion
    #region Function Errors (800xxx)
    
    public const string FunctionNotInWorkflow = "Function:800001";
    
    #endregion

    #region Authorization Errors (110xxx)

    /// <summary>Role denied for the requested transition or function.</summary>
    public const string AuthorizationRoleDenied = "Authorization:110001";

    /// <summary>Authorize requires exactly one of transitionKey, functionKey, or queryRoles (instance only).</summary>
    public const string AuthorizeRequiresExactlyOneTarget = "Authorization:110002";

    /// <summary>Query roles check is only valid for instance-level authorize.</summary>
    public const string AuthorizeQueryRolesRequiresInstance = "Authorization:110003";

    #endregion
    
    #region Discovery Errors (700xxx)
    
    public const string DomainEndpointNotFound = "Discovery:700001";
    public const string DomainDiscoveryFailed = "Discovery:700002";
    
    #endregion
}
