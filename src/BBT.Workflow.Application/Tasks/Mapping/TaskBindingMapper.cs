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
                SoapTask soap => (TaskTypes.Soap, (object)MapSoapTask(soap)),
                DaprServiceTask daprService => (TaskTypes.DaprService, MapDaprServiceTask(daprService)),
                DaprBindingTask daprBinding => (TaskTypes.DaprBinding, MapDaprBindingTask(daprBinding)),
                DaprHttpEndpointTask daprHttpEndpoint => (TaskTypes.DaprHttpEndpoint, MapDaprHttpEndpointTask(daprHttpEndpoint)),
                DaprPubSubTask daprPubSub => (TaskTypes.DaprPubSub, MapDaprPubSubTask(daprPubSub)),
                StateStoreTask stateStore => (TaskTypes.StateStore, (object)MapStateStoreTask(stateStore)),

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
        AcceptedStatusCodes = task.AcceptedStatusCodes
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
        TimeoutSeconds = task.TimeoutSeconds,
        AcceptedStatusCodes = task.AcceptedStatusCodes
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
        TimeoutSeconds = task.TimeoutSeconds,
        AcceptedStatusCodes = task.AcceptedStatusCodes
    };
    
    /// <summary>
    /// Maps GetInstancesTask to GetInstancesBinding.
    /// Note: Instance is resolved at runtime, this provides a basic mapping.
    /// When a runtime <see cref="GetInstancesTask.FilterSpec"/> is set, filter and sort are serialized
    /// from the spec; groupBy/aggregations travel inside the filter request envelope, exactly like a
    /// hand-written filter string.
    /// </summary>
    private static GetInstancesBinding MapGetInstancesTask(GetInstancesTask task) => new()
    {
        Domain = task.TriggerDomain,
        Workflow = task.TriggerFlow,
        Filter = task.FilterSpec is { } spec ? spec.ToFilterRequestJson() : task.Filter,
        Sort = task.FilterSpec?.ToSortJson(),
        Page = task.Page,
        PageSize = task.PageSize,
        ValidateSSL = task.ValidateSSL,
        UseDapr = task.UseDapr,
        Headers = task.Headers?.GetRawText(),
        TimeoutSeconds = task.TimeoutSeconds,
        AcceptedStatusCodes = task.AcceptedStatusCodes
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
        ETag = null,
        AcceptedStatusCodes = task.AcceptedStatusCodes
    };

    #endregion

    /// <summary>
    /// Maps SoapTask to SoapTaskBinding.
    /// </summary>
    private static SoapTaskBinding MapSoapTask(SoapTask task) => new()
    {
        Url = task.Url,
        SoapAction = task.SoapAction,
        SoapVersion = task.SoapVersion,
        Body = task.Body,
        Headers = task.Headers?.GetRawText(),
        TimeoutSeconds = task.TimeoutSeconds,
        ValidateSSL = task.ValidateSSL,
        AcceptedStatusCodes = task.AcceptedStatusCodes
    };

    /// <summary>
    /// Maps HttpTask to HttpTaskBinding.
    /// </summary>
    private static HttpTaskBinding MapHttpTask(HttpTask task)
    {
        var contentType = HttpContentType.Resolve(task.ContentType, ReadHeaderContentType(task.Headers));

        return new HttpTaskBinding
        {
            Url = task.Url,
            Method = task.Method,
            Headers = task.Headers?.GetRawText(),
            Body = task.RawBody ?? SerializeBody(task.Body, contentType),
            ContentType = task.ContentType,
            TimeoutSeconds = task.TimeoutSeconds,
            ValidateSSL = task.ValidateSSL,
            AcceptedStatusCodes = task.AcceptedStatusCodes
        };
    }

    /// <summary>
    /// Serializes the task body to its wire representation. JSON content types preserve the raw JSON text;
    /// for non-JSON content types a JSON-string body is unwrapped to its raw value (e.g. form-urlencoded
    /// "a=1&amp;b=2") so it is not transmitted with surrounding quotes.
    /// </summary>
    private static string? SerializeBody(JsonElement? body, string contentType)
    {
        if (body is not { } element)
            return null;

        if (!HttpContentType.IsJson(contentType) && element.ValueKind == JsonValueKind.String)
            return element.GetString();

        return element.GetRawText();
    }

    /// <summary>
    /// Reads the "Content-Type" entry (case-insensitive) from the task headers JSON, if present.
    /// </summary>
    private static string? ReadHeaderContentType(JsonElement? headers)
    {
        if (headers is not { ValueKind: JsonValueKind.Object } element)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "Content-Type", System.StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

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
        Body = task.Body?.GetRawText(),
        AcceptedStatusCodes = task.AcceptedStatusCodes
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
    /// Maps StateStoreTask to StateStoreBinding.
    /// Note: CacheKey → Key, CacheKeys → Keys; Value and Query serialized to raw JSON text.
    /// </summary>
    private static StateStoreBinding MapStateStoreTask(StateStoreTask task) => new()
    {
        Command = task.Command,
        StoreName = string.IsNullOrWhiteSpace(task.StoreName) ? null : task.StoreName,
        Key = string.IsNullOrWhiteSpace(task.CacheKey) ? null : task.CacheKey,
        Keys = task.CacheKeys?.ToList(),
        Query = task.Query.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : task.Query.GetRawText(),
        Value = task.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : task.Value.GetRawText(),
        TtlInSeconds = task.TtlInSeconds,
        ETag = task.ETag,
        Concurrency = task.Concurrency,
        Consistency = task.Consistency,
        Metadata = task.Metadata.ValueKind != JsonValueKind.Undefined
            ? task.Metadata.Deserialize<Dictionary<string, string>>()
            : null
    };

}
