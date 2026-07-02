namespace BBT.Workflow.Execution.Metrics;

/// <summary>
/// Simple metrics interface for task execution - independent of Domain.
/// </summary>
public interface ITaskMetrics
{
    /// <summary>
    /// Records a task execution attempt.
    /// </summary>
    void RecordTaskExecution(string taskType, string status);
    
    /// <summary>
    /// Records a Dapr service invocation.
    /// </summary>
    void RecordDaprServiceInvocation(string appId, string methodName, string status);
    
    /// <summary>
    /// Records a Dapr binding invocation.
    /// </summary>
    void RecordDaprBindingInvocation(string bindingName, string operation, string status);
    
    /// <summary>
    /// Records a Dapr PubSub message publish.
    /// </summary>
    void RecordDaprPubSubPublish(string pubSubName, string topic, string status);

    /// <summary>
    /// Records a notification task invocation.
    /// </summary>
    /// <param name="bindingName">The name of the notification binding component.</param>
    /// <param name="bindingKind">The kind of notification binding (Http, Mqtt, SignalR, etc.).</param>
    /// <param name="status">The status of the invocation (success, failure, cancelled).</param>
    void RecordNotificationInvocation(string bindingName, string bindingKind, string status);
}

/// <summary>
/// No-op implementation of ITaskMetrics.
/// Used when no metrics system is configured.
/// </summary>
public sealed class NullTaskMetrics : ITaskMetrics
{
    public static readonly ITaskMetrics Instance = new NullTaskMetrics();
    
    public void RecordTaskExecution(string taskType, string status) { }
    public void RecordDaprServiceInvocation(string appId, string methodName, string status) { }
    public void RecordDaprBindingInvocation(string bindingName, string operation, string status) { }
    public void RecordDaprPubSubPublish(string pubSubName, string topic, string status) { }
    public void RecordNotificationInvocation(string bindingName, string bindingKind, string status) { }
}

