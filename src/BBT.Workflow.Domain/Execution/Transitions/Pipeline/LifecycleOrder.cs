namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Defines the standard execution order constants for transition lifecycle steps.
/// These constants ensure a deterministic and documented execution sequence.
/// </summary>
public static class LifecycleOrder
{
    /// <summary>
    /// Subflow
    /// </summary>
    public const int Preflight = 5;
    
    /// <summary>
    /// Order for checking shared transitions in SubFlow states.
    /// Skips to CreateTransition if current state is SubFlow and transition is shared.
    /// </summary>
    public const int CheckParentUpdateDataTransition = ForwardToActiveSubflow - 1;
    
    /// <summary>
    /// Subflow
    /// </summary>
    public const int ForwardToActiveSubflow = 10;
    
    /// <summary>
    /// Order for setting instance to Busy status at pipeline start.
    /// Prevents concurrent modifications during transition processing.
    /// </summary>
    public const int SetBusy = CreateTransition - 1;
    
    /// <summary>
    /// Order for creating the transition record in the database.
    /// This should be the first step to track the transition attempt.
    /// </summary>
    public const int CreateTransition = 20;
    
    /// <summary>
    /// Order for executing transition's OnExecute tasks.
    /// These tasks run before the state change occurs.
    /// </summary>
    public const int OnExecute = 30;
    
    /// <summary>
    /// Order for canceling scheduled transition jobs before leaving current state.
    /// Ensures timer jobs are cleaned up when transitioning away (including self-transitions).
    /// Only cancels jobs for the current state's scheduled transitions.
    /// </summary>
    public const int CancelScheduledJobs = OnExit - 1;
    
    /// <summary>
    /// Order for executing current state's OnExit tasks.
    /// These tasks run when leaving the current state.
    /// </summary>
    public const int OnExit = 40;
    
    /// <summary>
    /// Order for changing the instance state.
    /// This is the core state transition operation.
    /// </summary>
    public const int ChangeState = 50;
    
    /// <summary>
    /// Order for executing target state's OnEntry tasks.
    /// These tasks run when entering the new state.
    /// </summary>
    public const int OnEntry = 60;
    
    /// <summary>
    /// Order for handling SubFlow operations.
    /// Manages sub-process initiation when state type is SubFlow.
    /// </summary>
    public const int SubFlow = 70;

    public const int ClearBusyOnResumeStep = Schedule - 1;
    
    /// <summary>
    /// Order for scheduling future transitions.
    /// Enqueues scheduled transitions based on timers.
    /// </summary>
    public const int Schedule = 80;
    
    /// <summary>
    /// Order for executing automatic transitions.
    /// Evaluates and triggers automatic transitions based on conditions.
    /// </summary>
    public const int Auto = 90;
    
    /// <summary>
    /// Order for handling workflow finishing.
    /// Manages workflow completion when state type is Finish.
    /// This should run after all other pipeline steps.
    /// </summary>
    public const int Finish = 100;
    
    /// <summary>
    /// Order for finalizing the transition.
    /// Updates transition record and performs cleanup operations.
    /// </summary>
    public const int Finalize = 110;
    
    public const int AfterEpilogueRefresh = Finalize + 1;
    
    /// <summary>
    /// Order for resolving Available status at pipeline end.
    /// Sets instance to Active when target state has only manual/event transitions.
    /// </summary>
    public const int ResolveAvailable = Finalize + 2;
}
