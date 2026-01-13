namespace BBT.Workflow.Execution;

/// <summary>
/// Task type constants for invoker registration and routing.
/// These must match the TaskType enum values (lowercased).
/// </summary>
public static class TaskTypes
{
    // Remote execution tasks
    public const string Http = "http";
    public const string DaprService = "daprservice";
    public const string DaprBinding = "daprbinding";
    public const string DaprHttpEndpoint = "daprhttpendpoint";
    public const string DaprPubSub = "daprpubsub";
    public const string Notification = "notification";

    // Trigger tasks (for cross-domain execution)
    public const string StartTrigger = "starttrigger";
    public const string DirectTrigger = "directtrigger";
    public const string SubProcess = "subprocess";
    public const string GetInstanceData = "getinstancedata";
    public const string GetInstances = "getinstances";
}

