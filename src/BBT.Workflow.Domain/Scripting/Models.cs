using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
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
    public Instance Instance { get; private set; }

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
    public Definitions.Workflow Workflow { get; private set; }

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

        public Builder SetWorkflow(Definitions.Workflow workflow)
        {
            _context.Workflow = workflow;
            return this;
        }

        public Builder SetInstance(Instance instance)
        {
            _context.Instance = instance;
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