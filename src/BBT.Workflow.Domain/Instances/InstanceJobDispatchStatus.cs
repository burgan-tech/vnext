namespace BBT.Workflow.Instances;

/// <summary>
/// Durable delivery/execution lifecycle for a tracked instance job.
/// </summary>
public enum InstanceJobDispatchStatus
{
    Scheduled = 0,
    PendingDispatch = 1,
    Processing = 2,
    Completed = 3,
    Failed = 4,
    Superseded = 5
}
