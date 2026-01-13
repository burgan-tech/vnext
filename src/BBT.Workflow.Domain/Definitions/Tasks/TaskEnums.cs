using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Task types
/// </summary>
[JsonConverter(typeof(StringToEnumJsonConverter<TaskType>))]
public enum TaskType
{
    DaprHttpEndpoint = 1,
    DaprBinding = 2,
    DaprService = 3,
    DaprPubSub = 4,
    Human = 5,
    Http = 6,
    Script = 7,
    Condition = 8,
    Timer = 9,
    Notification = 10,
    StartTrigger = 11,
    DirectTrigger = 12,
    GetInstanceData = 13,
    SubProcess = 14,
    GetInstances = 15
}

/// <summary>
/// Task triggers
/// </summary>
public enum TaskTrigger
{
    OnEntry = 1,
    OnExit = 2,
    Both = 3,
    Manual = 4,
    OnExecute = 5,
    Extension = 6
}

/// <summary>
/// Task statuses
/// </summary>
public enum TaskStatus
{
    Waiting = 1,
    Busy = 2,
    Completed = 3,
    Faulted = 4
}