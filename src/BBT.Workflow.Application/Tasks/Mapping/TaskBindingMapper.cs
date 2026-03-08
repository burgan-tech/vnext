using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// Maps Domain WorkflowTask instances to Execution Bindings.
/// Handles property name differences and type conversions between Domain and Execution models.
/// Uses the Result pattern for Railway-oriented error handling.
/// </summary>
public static class TaskBindingMapper
{
    /// <summary>
    /// Creates a TaskEnvelope from a WorkflowTask by mapping to the appropriate binding type.
    /// </summary>
    /// <param name="task">The workflow task to map.</param>
    /// <returns>A Result containing the TaskEnvelope with the appropriate binding, or an error.</returns>
    public static Result<TaskEnvelope> CreateEnvelope(WorkflowTask task)
    {
        return MapToBinding(task)
            .Map(result => new TaskEnvelope
            {
                TaskType = result.TaskType,
                TaskKey = task.Key,
                Binding = JsonSerializer.SerializeToElement(result.Binding, result.Binding.GetType())
            });
    }

    /// <summary>
    /// Maps a WorkflowTask to its corresponding binding type and task type string.
    /// </summary>
    private static Result<(string TaskType, object Binding)> MapToBinding(WorkflowTask task)
    {
        try
        {
            var result = task switch
            {
                // Remote execution tasks
                HttpTask http => (TaskTypes.Http, MapHttpTask(http)),
                DaprServiceTask daprService => (TaskTypes.DaprService, MapDaprServiceTask(daprService)),
                DaprBindingTask daprBinding => (TaskTypes.DaprBinding, MapDaprBindingTask(daprBinding)),
                DaprHttpEndpointTask daprHttpEndpoint => (TaskTypes.DaprHttpEndpoint, MapDaprHttpEndpointTask(daprHttpEndpoint)),
                DaprPubSubTask daprPubSub => (TaskTypes.DaprPubSub, MapDaprPubSubTask(daprPubSub)),
                NotificationTask notification => (TaskTypes.Notification, MapNotificationTask(notification)),
                
                // Trigger tasks (basic mapping - runtime context handled by invokers)
                StartTask startTask => (TaskTypes.StartTrigger, (object)MapStartTask(startTask)),
                DirectTriggerTask directTriggerTask => (TaskTypes.DirectTrigger, (object)MapDirectTriggerTask(directTriggerTask)),
                SubProcessTask subProcessTask => (TaskTypes.SubProcess, (object)MapSubProcessTask(subProcessTask)),
                GetInstancesTask getInstancesTask => (TaskTypes.GetInstances, (object)MapGetInstancesTask(getInstancesTask)),
                GetInstanceDataTask getDataTask => (TaskTypes.GetInstanceData, (object)MapGetInstanceDataTask(getDataTask)),
                
                // Note: DirectTriggerTask and SubProcessTask require runtime context (InstanceId, Correlation)
                // and should use ITriggerTaskRemoteExecutor directly with pre-built bindings
                _ => throw new NotSupportedException($"Task type {task.GetType().Name} is not supported for remote execution")
            };

            return Result<(string TaskType, object Binding)>.Ok(result);
        }
        catch (NotSupportedException ex)
        {
            return Result<(string TaskType, object Binding)>.Fail(
                Error.Validation(
                    WorkflowErrorCodes.UnsupportedTaskType,
                    ex.Message,
                    task.GetType().Name));
        }
        catch (Exception ex)
        {
            return Result<(string TaskType, object Binding)>.Fail(
                Error.Failure(
                    WorkflowErrorCodes.TaskBindingMappingFailed,
                    $"Failed to map task {task.Key} to binding: {ex.Message}",
                    task.Key));
        }
    }

    #region Trigger Task Mappings

    /// <summary>
    /// Maps StartTask to StartTriggerBinding.
    /// </summary>
    private static StartTriggerBinding MapStartTask(StartTask task) => new()
    {
        Domain = task.TriggerDomain,
        Workflow = task.TriggerFlow,
        Version = task.TriggerVersion,
        Body = task.Body,
        Tags = task.TriggerTags,
        Sync = task.TriggerSync,
        UseDapr = task.UseDapr,
        ValidateSSL = task.ValidateSSL,
        Headers = task.Headers?.GetRawText(),
        TimeoutSeconds = task.TimeoutSeconds,
    };
    
    /// <summary>
    /// Maps DirectTriggerTask to DirectTriggerBinding.
    /// </summary>
    private static DirectTriggerBinding MapDirectTriggerTask(DirectTriggerTask task) => new()
    {
        Domain = task.TriggerDomain,
        Workflow = task.TriggerFlow,
        InstanceId = task.TriggerInstanceId,
        Key = task.TriggerKey,
        TransitionName =  task.TransitionName,
        Body = task.Body,
        Tags = task.TriggerTags,
        Sync = task.TriggerSync,
        UseDapr = task.UseDapr,
        ValidateSSL = task.ValidateSSL,
        Headers = task.Headers?.GetRawText(),
        TimeoutSeconds = task.TimeoutSeconds
    };
    
    /// <summary>
    /// Maps SubProcessTask to SubProcessBinding.
    /// </summary>
    private static SubProcessBinding MapSubProcessTask(SubProcessTask task) => new()
    {
        Domain = task.TriggerDomain,
        Workflow = task.TriggerFlow,
        Version = task.TriggerVersion,
        Tags = task.TriggerTags,
        Key = task.TriggerKey,
        InstanceId = Guid.Empty,
        Body = task.Body,
        ExtraProperties = new Dictionary<string, object>(),
        Sync = task.TriggerSync,
        UseDapr = task.UseDapr,
        ValidateSSL = task.ValidateSSL,
        Headers = task.Headers?.GetRawText(),
        TimeoutSeconds = task.TimeoutSeconds
    };
    
    /// <summary>
    /// Maps GetInstancesTask to GetInstancesBinding.
    /// Note: Instance is resolved at runtime, this provides a basic mapping.
    /// </summary>
    private static GetInstancesBinding MapGetInstancesTask(GetInstancesTask task) => new()
    {
        Domain = task.TriggerDomain,
        Workflow = task.TriggerFlow,
        Filter = task.Filter,
        Page = task.Page,
        PageSize = task.PageSize,
        ValidateSSL = task.ValidateSSL,
        UseDapr = task.UseDapr,
        Headers = task.Headers?.GetRawText(),
        TimeoutSeconds = task.TimeoutSeconds
    };

    /// <summary>
    /// Maps GetInstanceDataTask to GetInstanceDataBinding.
    /// Note: Instance is resolved at runtime, this provides a basic mapping.
    /// </summary>
    private static GetInstanceDataBinding MapGetInstanceDataTask(GetInstanceDataTask task) => new()
    {
        Domain = task.TriggerDomain,
        Workflow = task.TriggerFlow,
        Instance = task.Identifier ?? string.Empty,
        Extensions = task.Extensions,
        ValidateSSL = task.ValidateSSL,
        UseDapr = task.UseDapr,
        Headers = task.Headers?.GetRawText(),
        TimeoutSeconds = task.TimeoutSeconds,
        ETag = null
    };

    #endregion

    /// <summary>
    /// Maps HttpTask to HttpTaskBinding.
    /// </summary>
    private static HttpTaskBinding MapHttpTask(HttpTask task) => new()
    {
        Url = task.Url,
        Method = task.Method,
        Headers = task.Headers?.GetRawText(),
        Body = task.Body?.GetRawText(),
        TimeoutSeconds = task.TimeoutSeconds,
        ValidateSSL = task.ValidateSSL
    };

    /// <summary>
    /// Maps DaprServiceTask to DaprServiceBinding.
    /// Note: HttpVerb → Method property mapping.
    /// </summary>
    private static DaprServiceBinding MapDaprServiceTask(DaprServiceTask task) => new()
    {
        AppId = task.AppId,
        MethodName = task.MethodName,
        Method = task.HttpVerb,  // HttpVerb → Method
        QueryString = task.QueryString,
        Headers = task.Headers?.GetRawText(),
        Body = task.Body?.GetRawText()
    };

    /// <summary>
    /// Maps DaprBindingTask to DaprBindingTaskBinding.
    /// Note: Data → Body property mapping.
    /// </summary>
    private static DaprBindingTaskBinding MapDaprBindingTask(DaprBindingTask task) => new()
    {
        BindingName = task.BindingName,
        Operation = task.Operation,
        Body = task.Data?.GetRawText(),  // Data → Body
        Metadata = task.Metadata.ValueKind != JsonValueKind.Undefined 
            ? task.Metadata.Deserialize<Dictionary<string, string>>() 
            : null
    };

    /// <summary>
    /// Maps DaprHttpEndpointTask to DaprHttpEndpointBinding.
    /// </summary>
    private static DaprHttpEndpointBinding MapDaprHttpEndpointTask(DaprHttpEndpointTask task) => new()
    {
        EndpointName = task.EndpointName,
        Path = task.Path,
        Method = task.Method,
        Body = task.Body?.GetRawText()
    };

    /// <summary>
    /// Maps DaprPubSubTask to DaprPubSubBinding.
    /// Note: Topic → TopicName, Data → Body property mappings.
    /// </summary>
    private static DaprPubSubBinding MapDaprPubSubTask(DaprPubSubTask task) => new()
    {
        PubSubName = task.PubSubName,
        TopicName = task.Topic,  // Topic → TopicName
        Body = task.Data.ValueKind != JsonValueKind.Undefined 
            ? task.Data.GetRawText() 
            : null,
        Metadata = task.Metadata.ValueKind != JsonValueKind.Undefined 
            ? task.Metadata.Deserialize<Dictionary<string, string>>() 
            : null
    };

    /// <summary>
    /// Maps NotificationTask to NotificationBinding.
    /// Body is serialized from the task's Body property (set by mapping).
    /// </summary>
    private static NotificationBinding MapNotificationTask(NotificationTask task) => new()
    {
        Body = task.Body != null ? JsonSerializer.Serialize(task.Body) : null,
        Subject = task.Subject,
        To = task.To,
        Metadata = task.Metadata?.ValueKind == JsonValueKind.Object
            ? task.Metadata.Value.Deserialize<Dictionary<string, string>>()
            : null
    };
}
