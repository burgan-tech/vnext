using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Related;
using BBT.Workflow.Shared.Merging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Represents the response model returned by mapping interfaces for workflow script execution.
/// This model is used to capture data modifications, audit information, and metadata during task processing,
/// subflow operations, and instance data transformations.
/// </summary>
/// <remarks>
/// ScriptResponse serves multiple purposes:
/// <list type="bullet">
/// <item><description>For IMapping: Captures task audit data and provides output data for instance merging</description></item>
/// <item><description>For ISubFlowMapping/ISubProcessMapping: Provides input data for subflow/subprocess creation</description></item>
/// <item><description>For ISubFlowMapping OutputHandler: Transforms completed subflow data for parent instance merging</description></item>
/// </list>
/// </remarks>
public sealed class ScriptResponse
{
    /// <summary>
    /// Unique identifier or key associated with the script response.
    /// Can be used for correlation, caching, or referencing purposes in workflow execution.
    /// </summary>
    /// <value>A string key that identifies this response, or null if no specific identification is needed.</value>
    public string? Key { get; set; }

    /// <summary>
    /// The primary data payload returned by the script execution.
    /// This data will be used differently based on the mapping interface context:
    /// - IMapping InputHandler: Task audit data for logging
    /// - IMapping OutputHandler: Instance data to be merged with current instance
    /// - ISubFlowMapping/ISubProcessMapping: Input parameters for subflow/subprocess creation
    /// - ISubFlowMapping OutputHandler: Transformed data from completed subflow to merge with parent instance
    /// </summary>
    /// <value>Dynamic data object containing the script execution results, or null if no data is produced.</value>
    public dynamic? Data { get; set; }

    /// <summary>
    /// HTTP headers or metadata headers associated with the response.
    /// Useful for passing additional context information, authentication tokens, or custom metadata.
    /// </summary>
    /// <value>Dynamic object containing header information, or null if no headers are needed.</value>
    public dynamic? Headers { get; set; }

    /// <summary>
    /// Optional HTTP status code to apply to the function response.
    /// Allows multi-task output handlers to override the default 200 status (e.g. 400/404/410).
    /// When null, the engine falls back to single-task metadata or the default status code.
    /// </summary>
    /// <value>An HTTP status code, or null to use the default behavior.</value>
    public int? StatusCode { get; set; }

    /// <summary>
    /// Route values or routing parameters associated with the response.
    /// Can be used for workflow routing decisions, URL generation, or parameter passing between workflow components.
    /// </summary>
    /// <value>Dynamic object containing route values, or null if no routing information is provided.</value>
    public dynamic? RouteValues { get; set; }

    /// <summary>
    /// Collection of tags for categorizing, filtering, or marking the response.
    /// Tags can be used for workflow analytics, debugging, conditional processing, or organizational purposes.
    /// </summary>
    /// <value>Array of string tags. Initialize as empty array if no tags are needed.</value>
    public string[] Tags { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Standardized task execution response that provides consistent structure for all task types.
/// This model includes execution status, data, metadata, and error information.
/// </summary>
public sealed class StandardTaskResponse
{
    /// <summary>
    /// The actual response data from the task execution.
    /// </summary>
    public dynamic? Data { get; set; }

    /// <summary>
    /// HTTP status code for HTTP-based tasks (HttpTask, DaprServiceTask).
    /// </summary>
    public int? StatusCode { get; set; }

    /// <summary>
    /// Indicates whether the task execution was successful.
    /// </summary>
    public bool IsSuccess { get; set; } = true;

    /// <summary>
    /// Error message if task execution failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Response headers for HTTP-based tasks.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Additional metadata about the task execution.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Task execution duration in milliseconds.
    /// </summary>
    public long? ExecutionDurationMs { get; set; }

    /// <summary>
    /// Task type identifier.
    /// </summary>
    public string? TaskType { get; set; }

    /// <summary>
    /// Raw response body as a string. For SOAP/XML tasks this is the unparsed XML string;
    /// for HTTP tasks it is the raw JSON/text payload. Use <c>ParseXml(Body)</c> in scripts
    /// to convert to an <c>XmlDocument</c>.
    /// </summary>
    public string? Body { get; set; }
}

/// <summary>
/// Represents the original transition request data and headers persisted at transition record creation.
/// Exposed on <see cref="ScriptContext.CurrentTransition"/> so mapping scripts can access the initial
/// request payload regardless of later Body merges from task responses.
/// </summary>
/// <remarks>
/// Header keys are normalized to lowercase for consistent access (e.g. context.CurrentTransition.Header.authorization).
/// </remarks>
public sealed class ScriptTransitionRequest
{
    /// <summary>
    /// Original transition request body (dynamic, typically ExpandoObject from JSON).
    /// </summary>
    public dynamic? Data { get; }

    /// <summary>
    /// Original transition request headers with all keys normalized to lowercase.
    /// </summary>
    public dynamic? Header { get; }

    /// <summary>
    /// Creates a new instance with the given data and header.
    /// </summary>
    public ScriptTransitionRequest(dynamic? data, dynamic? header)
    {
        Data = data;
        Header = header;
    }
}

/// <summary>
/// Lightweight projection of incident information exposed to workflow scripts.
/// Provides awareness of active error incidents without exposing the full incident history.
/// </summary>
public sealed class ScriptIncidentInfo
{
    /// <summary>Whether the instance has at least one unresolved incident.</summary>
    public bool HasActiveIncident { get; init; }

    /// <summary>The most recent unresolved incident (null if all resolved).</summary>
    public InstanceIncident? ActiveIncident { get; init; }

    /// <summary>Total number of incidents (resolved + unresolved) retained on the instance.</summary>
    public int TotalIncidentCount { get; init; }
}

public class ScriptContext(ILogger<ScriptContext> logger) : IDisposable, IAsyncDisposable
{
    public static readonly JsonSerializerOptions JsonScriptBodyOptions = new()
    {
        Converters = { new ExpandoObjectJsonConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            try
            {
                TaskResponse.Clear();
                OutputResponse.Clear();
                MetaData.Clear();
                Definitions.Clear();

                Body = null;
                Headers = null;
                RouteValues = null;
                CurrentTransition = null;
                Incident = null;

                // Backing field, not the property: the property getter throws once disposed, and
                // Dispose must stay callable twice.
                if (_related is RelatedInstanceAccessor accessor)
                    accessor.ClearMemo();

                _related = NullRelatedInstanceAccessor.Instance;
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Error clearing collections during disposal");
            }
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Throws ObjectDisposedException if the context has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ScriptContext));
    }

    /// <summary>
    /// Contains the request payload data from workflow transitions or task execution responses,
    /// with all property names automatically converted to camelCase format for consistency.
    /// This dynamic object can hold either the incoming transition request data or the processed
    /// <see cref="StandardTaskResponse"/> data from completed tasks.
    /// </summary>
    /// <value>
    /// A dynamic object containing:
    /// - For transition requests: The payload data sent with the transition (converted to camelCase)
    /// - For task responses: The processed <see cref="StandardTaskResponse"/> containing execution results
    /// - Can be null if no body data is available
    /// 
    /// When containing <see cref="StandardTaskResponse"/> data, includes:
    /// - data: The actual response data from task execution
    /// - statusCode: HTTP status code for HTTP-based tasks
    /// - isSuccess: Boolean indicating execution success
    /// - errorMessage: Error details if task execution failed
    /// - headers: Response headers for HTTP-based tasks
    /// - metadata: Additional execution metadata
    /// - executionDurationMs: Task execution time in milliseconds
    /// - taskType: Task type identifier
    /// </value>
    /// <remarks>
    /// The Body property serves as the primary data container in script contexts. It's automatically
    /// populated with request data during transitions and updated with task results during execution.
    /// All data is processed through JSON serialization with camelCase property naming policy to ensure
    /// consistent naming conventions across the entire workflow execution context.
    /// Use SetBody() or SetStandardResponse() methods to modify this property safely.
    /// </remarks>
    public dynamic? Body { get; private set; }

    /// <summary>
    /// Contains HTTP headers from transition requests with all header keys normalized to lowercase.
    /// This includes both standard HTTP headers and custom application-specific headers.
    /// </summary>
    /// <value>
    /// A dynamic object containing header key-value pairs where:
    /// - All header keys are automatically converted to lowercase for consistency
    /// - Values preserve their original casing and format
    /// - Can be null if no headers are present
    /// </value>
    /// <remarks>
    /// Common headers include authentication tokens, content-type information, correlation IDs,
    /// and custom business headers. The lowercase normalization ensures consistent header access
    /// across different HTTP clients and frameworks.
    /// </remarks>
    public dynamic? Headers { get; private set; }

    /// <summary>
    /// Contains route values and URL parameters extracted from the transition request.
    /// These values are typically derived from URL path segments and query parameters.
    /// </summary>
    /// <value>
    /// A dynamic object containing routing parameter key-value pairs:
    /// - Path segment values (e.g., /workflow/{workflowId}/instance/{instanceId})
    /// - Query string parameters
    /// - Custom routing values set by the application
    /// - Can be null if no route values are available
    /// </value>
    /// <remarks>
    /// Route values are essential for workflows that need to access URL-based parameters,
    /// such as entity IDs, filter criteria, or navigation context. They provide a way
    /// to pass structured data through the URL routing mechanism.
    /// </remarks>
    public dynamic? RouteValues { get; private set; }

    /// <summary>
    /// Contains query string parameters extracted from the HTTP request.
    /// These values are derived from URL query string parameters.
    /// </summary>
    /// <value>
    /// A dynamic object containing query parameter key-value pairs:
    /// - Query string parameters (e.g., ?filter=value&sort=asc)
    /// - Can be null if no query parameters are available
    /// </value>
    /// <remarks>
    /// Query parameters provide a way to pass filtering, sorting, pagination,
    /// and other request-specific data through the URL query string.
    /// </remarks>
    public dynamic? QueryParameters { get; private set; }

    /// <summary>
    /// The raw inbound external event payload (pub/sub message / input-binding body) made available to
    /// <see cref="IEventMapping"/> during event-driven start/transition. Null outside event-driven executions.
    /// </summary>
    public dynamic? EventPayload { get; private set; }

    /// <summary>
    /// The original, unmodified request body exactly as received (a literal string, NOT camelCased or
    /// re-serialized like <see cref="Body"/>). Intended for signature verification (JWS / mTLS) where the
    /// payload must match the bytes that were signed. Null when no raw body is available for the current
    /// execution (e.g. internal executions with neither an HTTP request nor a job scope).
    /// </summary>
    public string? RawBody { get; private set; }

    /// <summary>
    /// The active workflow instance that is currently being processed or executed.
    /// This represents the live instance with its current state, data, and execution history.
    /// </summary>
    /// <value>
    /// An <see cref="Instance"/> object containing:
    /// - Current state information and workflow position
    /// - Instance data accumulated throughout execution
    /// - Execution history and audit trail
    /// - Correlation information and metadata
    /// </value>
    /// <remarks>
    /// The Instance property provides access to the complete workflow instance context,
    /// including its current state, accumulated data, and execution history. This is
    /// essential for making context-aware decisions in mapping implementations.
    /// </remarks>
    public Instance? Instance { get; private set; }

    /// <summary>
    /// Accumulates controlled mutations for <see cref="Instance"/> properties
    /// that scripts are allowed to change (e.g. Stage). Mutations are applied
    /// atomically by <c>ApplyScriptContextChanges</c> after script execution.
    /// </summary>
    public InstanceMutations Mutations { get; } = new();

    /// <summary>
    /// Error boundary incident information for the current instance.
    /// Provides awareness of active incidents so scripts can implement
    /// compensating logic or display error context to users.
    /// </summary>
    /// <value>
    /// A <see cref="ScriptIncidentInfo"/> containing:
    /// - HasActiveIncident: whether any unresolved incident exists
    /// - ActiveIncident: the most recent unresolved incident (with error details)
    /// - TotalIncidentCount: total incidents retained on the instance
    /// Null when no instance is loaded in the script context.
    /// </value>
    public ScriptIncidentInfo? Incident { get; private set; }

    /// <summary>
    /// Access to instances related to <see cref="Instance"/> — one hop up (the parent that started this
    /// instance as a SubFlow/SubProcess) or one hop down (this instance's own correlations).
    /// Nothing is pre-fetched; the first call that needs data performs the read, and results are
    /// memoized until this context is disposed.
    /// </summary>
    /// <remarks>
    /// Reads are unfiltered by design (no query-role check, no x-roles field filtering, no extensions).
    /// Copying a related instance's field into this instance's data therefore makes that field reachable
    /// by any client entitled to read this instance — x-roles protection does not follow the copy, so
    /// copy only the fields you intend to expose. Every cross-domain read is logged
    /// (<c>RelatedInstanceCrossDomainRead</c>, event id 20432).
    /// Never null: defaults to <see cref="NullRelatedInstanceAccessor"/> when no reader is wired.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The context has been disposed.</exception>
    public IRelatedInstanceAccessor Related
    {
        get
        {
            // Deliberately guarded, unlike Body/Incident which merely go null. Those are nullable
            // values; this is an accessor whose contract is "null means no parent". Handing back the
            // null accessor after disposal would answer HasParent == false — a definite claim, not an
            // absence.
            ThrowIfDisposed();
            return _related;
        }
        private set => _related = value;
    }

    private IRelatedInstanceAccessor _related = NullRelatedInstanceAccessor.Instance;

    /// <summary>
    /// The workflow definition that describes the structure, states, transitions, and tasks
    /// for the current workflow execution context.
    /// </summary>
    /// <value>
    /// A <see cref="Definitions.Workflow"/> object containing:
    /// - Workflow structure and state definitions
    /// - Available transitions and their conditions
    /// - Task definitions and configurations
    /// - Workflow metadata and properties
    /// </value>
    /// <remarks>
    /// The Workflow property provides access to the complete workflow blueprint,
    /// enabling mapping implementations to understand the workflow structure,
    /// available transitions, and task configurations for informed decision making.
    /// </remarks>
    public Definitions.Workflow? Workflow { get; private set; }

    /// <summary>
    /// Provides runtime information and services for the current execution context,
    /// including environment details, configuration, and operational capabilities.
    /// </summary>
    /// <value>
    /// An <see cref="IRuntimeInfoProvider"/> interface providing:
    /// - Current execution environment information
    /// - Runtime configuration and settings
    /// - Service discovery and dependency access
    /// - Operational context and capabilities
    /// </value>
    /// <remarks>
    /// The Runtime property enables mapping implementations to access environment-specific
    /// information, configuration settings, and runtime services needed for context-aware
    /// processing and integration with external systems.
    /// </remarks>
    public IRuntimeInfoProvider Runtime { get; private set; }

    /// <summary>
    /// The current transition being processed, containing information about the state change,
    /// triggers, conditions, and associated tasks.
    /// </summary>
    /// <value>
    /// A <see cref="Transition"/> object containing:
    /// - Source and target state information
    /// - Transition triggers and conditions
    /// - Associated tasks and their configurations
    /// - Transition metadata and properties
    /// </value>
    /// <remarks>
    /// The Transition property provides detailed information about the current state change
    /// being executed, including its configuration, conditions, and associated tasks.
    /// This is particularly useful for transition-specific logic and task processing.
    /// </remarks>
    public Transition Transition { get; private set; }

    /// <summary>
    /// The original transition request data and headers that initiated this transition.
    /// Populated from the persisted <see cref="Instances.InstanceTransition"/> when ScriptContext is built
    /// during the transition pipeline (OnExecute, OnExit, OnEntry). Null when no transition record exists
    /// (e.g. initial creation, queries, scheduled/auto transitions).
    /// </summary>
    /// <value>
    /// - Data: Original request body (dynamic)
    /// - Header: Original request headers with lowercase keys (dynamic)
    /// - Null when ScriptContext is built outside transition task steps
    /// </value>
    public ScriptTransitionRequest? CurrentTransition { get; private set; }

    /// <summary>
    /// Contains workflow and component definitions available in the current execution context.
    /// This includes reusable definitions, templates, and configuration objects.
    /// </summary>
    /// <value>
    /// A dictionary containing definition key-value pairs where:
    /// - Keys are definition identifiers or names
    /// - Values are definition objects or configuration data
    /// - Provides access to reusable workflow components and templates
    /// </value>
    /// <remarks>
    /// The Definitions property enables access to shared workflow components, templates,
    /// and configuration objects that can be reused across different workflow instances
    /// and execution contexts.
    /// </remarks>
    public Dictionary<string, dynamic> Definitions { get; private set; } = new();

    /// <summary>
    /// Contains the execution results and responses from completed workflow tasks,
    /// with task keys converted to follow variable naming standards and values containing
    /// <see cref="ScriptResponse"/> objects as data payload.
    /// This collection is populated as tasks complete and is used by output handlers
    /// to process and transform task results.
    /// </summary>
    /// <value>
    /// A dictionary containing task response data where:
    /// - Keys are task identifiers converted to proper variable naming standards (camelCase, alphanumeric)
    /// - Values are <see cref="ScriptResponse"/> objects containing:
    ///   * Key: Unique identifier for the task response
    ///   * Data: The actual task execution results and response data
    ///   * Headers: HTTP headers or metadata headers from task execution
    ///   * RouteValues: Routing parameters or configuration values
    ///   * Tags: Categorization tags for monitoring and auditing
    /// - Updated automatically as tasks complete execution
    /// - Can contain null values for tasks that produce no output
    /// </value>
    /// <remarks>
    /// The TaskResponse collection is essential for output handlers in IMapping implementations,
    /// providing access to task execution results that need to be processed, transformed,
    /// and integrated into the workflow instance data. Task keys are normalized to ensure
    /// consistent variable naming conventions, making them safe to use in script contexts
    /// and dynamic property access scenarios.
    /// </remarks>
    public Dictionary<string, dynamic?> TaskResponse { get; private set; } = new();

    public Dictionary<string, dynamic?> OutputResponse { get; private set; } = new();

    /// <summary>
    /// Contains execution metadata, performance metrics, and contextual information
    /// about the current task, transition, or workflow execution.
    /// </summary>
    /// <value>
    /// A dictionary containing metadata key-value pairs such as:
    /// - Execution timing and performance metrics
    /// - Processing context and environment information
    /// - Audit and tracking data
    /// - Custom metadata set by mapping implementations
    /// </value>
    /// <remarks>
    /// The MetaData collection provides a flexible way to store and access execution
    /// context information, performance metrics, and custom data that supports
    /// monitoring, auditing, and debugging of workflow executions.
    /// </remarks>
    public Dictionary<string, dynamic> MetaData { get; private set; } = new();

    /// <summary>
    /// Sets the body of the script context. This method is thread-safe and can be used
    /// for context synchronization in distributed scenarios.
    /// </summary>
    /// <param name="body">The new body content.</param>
    public void SetBody(object? body)
    {
        ThrowIfDisposed();
        MergeToBody(body, JsonSerializerConstants.JsonOptions);
    }

    /// <summary>
    /// Re-bases the frozen <see cref="Instance"/> snapshot onto the supplied live instance.
    /// Used after a state change within the same transition so that downstream steps
    /// (e.g. OnEntry tasks and state-level error boundary resolution) observe the new
    /// <c>CurrentState</c> instead of the stale snapshot captured before the change.
    /// Mirrors <c>Builder.SetInstance</c>: takes a fresh snapshot and recomputes the
    /// <see cref="Incident"/> projection. Accumulated <see cref="TaskResponse"/>,
    /// <see cref="OutputResponse"/> and <see cref="MetaData"/> are intentionally preserved.
    /// </summary>
    /// <param name="instance">The live instance whose current state should be reflected.</param>
    public void RefreshInstance(Instance instance)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(instance);

        var snapshot = instance.CreateSnapshot();
        Instance = snapshot;
        Incident = new ScriptIncidentInfo
        {
            HasActiveIncident = snapshot.HasActiveIncident,
            ActiveIncident = snapshot.Incidents.LastOrDefault(i => !i.IsResolved),
            TotalIncidentCount = snapshot.Incidents.Count
        };
    }

    /// <summary>
    /// Sets the standardized response body for the script context.
    /// </summary>
    /// <param name="response">The standardized task response.</param>
    /// <param name="taskKey">The standardized task variable name.</param>
    public void SetStandardResponse(StandardTaskResponse response, string? taskKey = null)
    {
        ThrowIfDisposed();
        var value = MergeToBody(response, JsonScriptBodyOptions);
        if (!string.IsNullOrWhiteSpace(taskKey) && value != null)
        {
            TaskResponse[taskKey!] = value;
        }
    }

    /// <summary>
    /// Sets the output response data directly without merging to Body.
    /// If <paramref name="taskKey"/> is null or whitespace, the method has no effect.
    /// </summary>
    /// <param name="output">The output data.</param>
    /// <param name="taskKey">The standardized task variable name. If null or whitespace, no action is taken.</param>
    public void SetOutputResponse(object? output, string? taskKey = null)
    {
        ThrowIfDisposed();

        // Only set output if taskKey is provided
        if (!string.IsNullOrWhiteSpace(taskKey))
        {
            var value = ToDynamic(output, JsonScriptBodyOptions);
            if (value != null)
            {
                OutputResponse[taskKey!] = value;
            }
        }
    }

    /// <summary>
    /// Converts an object to dynamic without merging to Body.
    /// Avoids double serialization when data doesn't need to be merged.
    /// </summary>
    /// <param name="content">The content to convert.</param>
    /// <param name="jsonOptions">The JSON serialization options to use.</param>
    /// <returns>The dynamic representation of the content, or null if content is null.</returns>
    private static dynamic? ToDynamic(object? content, JsonSerializerOptions jsonOptions)
    {
        if (content == null)
        {
            return null;
        }

        var serializedContent = JsonSerializer.Serialize(content, jsonOptions);
        using var document = JsonDocument.Parse(serializedContent);
        return document.RootElement.ToDynamic();
    }

    /// <summary>
    /// Merges the provided object into the existing Body using the specified JSON options.
    /// If Body is null, it initializes it with the new content.
    /// Supports both JSON objects and JSON arrays with full merge capabilities.
    /// </summary>
    /// <param name="content">The content to merge into Body.</param>
    /// <param name="jsonOptions">The JSON serialization options to use.</param>
    private dynamic? MergeToBody(object? content, JsonSerializerOptions jsonOptions)
    {
        var newValue = ToDynamic(content, jsonOptions);

        if (newValue == null)
        {
            return null;
        }

        // Use ObjectMerger for all merge operations - handles arrays, objects, and mixed types
        Body = ObjectMerger.MergeValues(Body, newValue);
        return newValue;
    }

    /// <summary>
    /// Creates a private execution copy for a parallel task branch. Mutable collections and the
    /// instance snapshot are copied so concurrent mappings never write to the caller's context.
    /// </summary>
    public ScriptContext CreateParallelBranch()
    {
        ThrowIfDisposed();

        var branch = new ScriptContext(logger)
        {
            Body = CloneDynamic(Body),
            Headers = CloneDynamic(Headers),
            RouteValues = CloneDynamic(RouteValues),
            QueryParameters = CloneDynamic(QueryParameters),
            EventPayload = CloneDynamic(EventPayload),
            RawBody = RawBody,
            Instance = Instance?.CreateSnapshot(),
            Workflow = Workflow,
            Runtime = Runtime,
            Transition = Transition,
            CurrentTransition = CurrentTransition,
            Definitions = new Dictionary<string, dynamic>(Definitions),
            TaskResponse = CloneDictionary(TaskResponse),
            OutputResponse = CloneDictionary(OutputResponse),
            MetaData = CloneMetadata(MetaData)
        };

        if (branch.Instance != null)
        {
            branch.Incident = new ScriptIncidentInfo
            {
                HasActiveIncident = branch.Instance.HasActiveIncident,
                ActiveIncident = branch.Instance.Incidents.LastOrDefault(incident => !incident.IsResolved),
                TotalIncidentCount = branch.Instance.Incidents.Count
            };
        }

        if (Mutations.HasStageChange)
            branch.Mutations.SetStage(Mutations.Stage);

        // A real accessor is bound to a specific instance, so a branch without one must not inherit
        // it — that would answer the branch's questions from the coordinator's instance.
        if (Related is RelatedInstanceAccessor branchSource && branch.Instance != null)
            branch.Related = branchSource.ForBranch(branch.Instance);
        else if (Related is RelatedInstanceAccessor && branch.Instance == null)
            branch.Related = NullRelatedInstanceAccessor.Instance;
        else
            branch.Related = Related;

        return branch;
    }

    /// <summary>
    /// Deterministically merges one completed parallel branch into this context.
    /// The coordinator calls this method in task definition order.
    /// </summary>
    public void MergeParallelBranch(ScriptContext branch)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(branch);

        MergeDictionary(TaskResponse, branch.TaskResponse, mergeIntoBody: true);
        MergeDictionary(OutputResponse, branch.OutputResponse, mergeIntoBody: false);
        MergeMetadata(branch.MetaData);

        if (branch.Mutations.HasStageChange)
        {
            if (Mutations.HasStageChange && !string.Equals(Mutations.Stage, branch.Mutations.Stage, StringComparison.Ordinal))
                throw new InvalidOperationException("Parallel tasks produced conflicting instance Stage mutations.");
            Mutations.SetStage(branch.Mutations.Stage);
        }

        var branchData = branch.Instance?.LatestData;
        if (Instance != null && branchData != null && branchData.Id != Instance.LatestData?.Id)
            Instance.AddData(Guid.NewGuid(), branchData.Data, VersionStrategy.IncreasePatch);
    }

    private void MergeDictionary(
        Dictionary<string, dynamic?> target,
        Dictionary<string, dynamic?> source,
        bool mergeIntoBody)
    {
        foreach (var (key, value) in source)
        {
            if (target.TryGetValue(key, out var existing))
            {
                if (!JsonEquivalent(existing, value))
                    throw new InvalidOperationException($"Parallel tasks produced conflicting output for key '{key}'.");
                continue;
            }

            var cloned = CloneDynamic(value);
            target.Add(key, cloned);
            if (mergeIntoBody)
                SetBody(cloned);
        }
    }

    private static Dictionary<string, dynamic?> CloneDictionary(
        IEnumerable<KeyValuePair<string, dynamic?>> source)
    {
        var clone = new Dictionary<string, dynamic?>();
        foreach (var (key, value) in source)
            clone.Add(key, CloneDynamic(value));
        return clone;
    }

    private static Dictionary<string, dynamic> CloneMetadata(
        IEnumerable<KeyValuePair<string, dynamic>> source) =>
        source.ToDictionary(pair => pair.Key, pair => (dynamic)CloneDynamic(pair.Value)!);

    private void MergeMetadata(Dictionary<string, dynamic> source)
    {
        foreach (var (key, value) in source)
        {
            if (MetaData.TryGetValue(key, out var existing))
            {
                if (!JsonEquivalent(existing, value))
                    throw new InvalidOperationException($"Parallel tasks produced conflicting metadata for key '{key}'.");
                continue;
            }

            MetaData.Add(key, CloneDynamic(value)!);
        }
    }

    private static dynamic? CloneDynamic(object? value) => value == null
        ? null
        : ToDynamic(value, JsonScriptBodyOptions);

    private static bool JsonEquivalent(object? left, object? right) =>
        JsonSerializer.Serialize(left, JsonScriptBodyOptions) ==
        JsonSerializer.Serialize(right, JsonScriptBodyOptions);

    public sealed class Builder(ILogger<ScriptContext> logger)
    {
        private readonly ScriptContext _context = new(logger);

        public Builder SetBody(object? body)
        {
            _context.SetBody(body);
            return this;
        }

        public Builder SetHeaders(object? headers)
        {
            _context.Headers = headers;
            return this;
        }

        public Builder SetRouteValues(object? routeValues)
        {
            _context.RouteValues = routeValues;
            return this;
        }

        public Builder SetQueryParameters(object? queryParameters)
        {
            _context.QueryParameters = queryParameters;
            return this;
        }

        /// <summary>
        /// Sets the raw inbound event payload consumed by <see cref="IEventMapping"/>.
        /// </summary>
        public Builder SetEventPayload(object? eventPayload)
        {
            _context.EventPayload = eventPayload;
            return this;
        }

        /// <summary>
        /// Sets the original raw request body (literal string, no re-serialization) for signature verification.
        /// </summary>
        public Builder SetRawBody(string? rawBody)
        {
            _context.RawBody = rawBody;
            return this;
        }

        public Builder SetWorkflow(Definitions.Workflow workflow)
        {
            _context.Workflow = workflow;
            return this;
        }

        public Builder SetInstance(Instance instance)
        {
            _context.Instance = instance;
            _context.Incident = new ScriptIncidentInfo
            {
                HasActiveIncident = instance.HasActiveIncident,
                ActiveIncident = instance.Incidents.LastOrDefault(i => !i.IsResolved),
                TotalIncidentCount = instance.Incidents.Count
            };
            return this;
        }

        /// <summary>
        /// Sets the related-instance accessor. When omitted, the context uses
        /// <see cref="NullRelatedInstanceAccessor"/> and reports no parent and no correlations.
        /// </summary>
        public Builder SetRelated(IRelatedInstanceAccessor? related)
        {
            if (related != null)
                _context.Related = related;

            return this;
        }

        public Builder SetTransition(Transition? transition)
        {
            if (transition != null)
            {
                _context.Transition = transition;
            }

            return this;
        }

        public Builder SetRuntime(IRuntimeInfoProvider runtime)
        {
            _context.Runtime = runtime;
            return this;
        }

        public Builder SetDefinitions(Dictionary<string, object> definitions)
        {
            _context.Definitions = definitions;
            return this;
        }

        public Builder SetTaskResponse(Dictionary<string, object?> taskResponse)
        {
            _context.TaskResponse = taskResponse;
            return this;
        }

        public Builder SetOutputResponse(Dictionary<string, object?> outputResponse)
        {
            _context.OutputResponse = outputResponse;
            return this;
        }

        public Builder SetMetadata(Dictionary<string, object> metadata)
        {
            _context.MetaData = metadata;
            return this;
        }

        /// <summary>
        /// Sets the current transition request (original body and headers) on the script context.
        /// </summary>
        public Builder SetCurrentTransition(ScriptTransitionRequest? value)
        {
            _context.CurrentTransition = value;
            return this;
        }

        public ScriptContext Build()
        {
            return _context;
        }
    }

    /// <summary>
    /// Asynchronously releases managed resources used by the ScriptContext.
    /// Implements IAsyncDisposable pattern for proper async cleanup.
    /// </summary>
    /// <returns>A ValueTask representing the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        // Perform synchronous cleanup through existing Dispose logic
        Dispose(true);

        // Suppress finalization since we've already cleaned up
        GC.SuppressFinalize(this);

        // No async operations needed currently, but await to satisfy compiler
        await ValueTask.CompletedTask;
    }
}
