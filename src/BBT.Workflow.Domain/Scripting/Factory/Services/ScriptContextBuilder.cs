using System.Dynamic;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Implementation of the fluent builder for constructing ScriptContext instances.
/// This builder handles both synchronous data and asynchronous data retrieval operations.
/// </summary>
internal sealed class ScriptContextBuilder(
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    ILogger<ScriptContext> logger) : IScriptContextBuilder
{
    private IRuntimeInfoProvider? _runtimeInfoProvider;
    private Definitions.Workflow? _workflow;
    private Instance? _instance;
    private Transition? _transition;
    private object? _body;
    private Dictionary<string, string?>? _headers;
    private Dictionary<string, object?>? _routeValues;
    private Dictionary<string, object?>? _queryParameters;
    private Dictionary<string, object?> _taskResponse = new();
    private Dictionary<string, object?> _outputResponse = new();
    private Dictionary<string, object> _metadata = new();
    private Dictionary<string, object> _definitions = new();

    // Async data retrieval properties
    private string? _workflowDomain;
    private string? _workflowKey;
    private string? _workflowVersion;
    private IReference? _workflowReference;
    private Guid? _instanceId;
    private bool _noTracking;
    private string? _transitionKey;
    private InstanceTransition? _instanceTransition;

    public IScriptContextBuilder WithRuntime(IRuntimeInfoProvider runtimeInfoProvider)
    {
        _runtimeInfoProvider = runtimeInfoProvider;
        return this;
    }

    public IScriptContextBuilder WithWorkflow(string domain, string workflowKey, string? version = null)
    {
        _workflowDomain = domain;
        _workflowKey = workflowKey;
        _workflowVersion = version;
        _workflow = null; // Clear direct workflow if set
        _workflowReference = null; // Clear reference if set
        return this;
    }

    public IScriptContextBuilder WithWorkflow(Definitions.Workflow workflow)
    {
        _workflow = workflow;
        _workflowDomain = null; // Clear async retrieval properties
        _workflowKey = null;
        _workflowVersion = null;
        _workflowReference = null;
        return this;
    }

    public IScriptContextBuilder WithWorkflow(IReference workflowReference)
    {
        _workflowReference = workflowReference;
        _workflow = null; // Clear direct workflow if set
        _workflowDomain = null; // Clear domain/key properties
        _workflowKey = null;
        _workflowVersion = null;
        return this;
    }

    public IScriptContextBuilder WithInstance(Guid instanceId, bool noTracking = false)
    {
        _instanceId = instanceId;
        _noTracking = noTracking;
        _instance = null; // Clear direct instance if set
        return this;
    }

    public IScriptContextBuilder WithInstance(Instance? instance)
    {
        if (instance == null)
        {
            return this;
        }
        _instance = instance.CreateSnapshot();
        _instanceId = null; // Clear async retrieval property
        return this;
    }

    public IScriptContextBuilder WithTransition(string? transitionKey)
    {
        _transitionKey = transitionKey;
        _transition = null; // Clear direct transition if set
        return this;
    }

    public IScriptContextBuilder WithTransition(Transition? transition)
    {
        if (transition == null)
        {
            return this;
        }
        _transition = transition;
        _transitionKey = null; // Clear async retrieval property
        return this;
    }

    public IScriptContextBuilder WithBody(object? body)
    {
        _body = body;
        return this;
    }

    public IScriptContextBuilder WithHeaders(Dictionary<string, string?>? headers)
    {
        _headers = headers;
        return this;
    }

    public IScriptContextBuilder WithRouteValues(Dictionary<string, object?>? routeValues)
    {
        _routeValues = routeValues;
        return this;
    }

    public IScriptContextBuilder WithRouteValues(Dictionary<string, string?>? routeValues)
    {
        _routeValues = routeValues?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);
        return this;
    }

    public IScriptContextBuilder WithQueryParameters(Dictionary<string, object?>? queryParameters)
    {
        _queryParameters = queryParameters;
        return this;
    }

    public IScriptContextBuilder WithQueryParameters(Dictionary<string, string?>? queryParameters)
    {
        _queryParameters = queryParameters?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);
        return this;
    }

    public IScriptContextBuilder WithTaskResponse(Dictionary<string, object?> taskResponse)
    {
        _taskResponse = taskResponse;
        return this;
    }
    
    public IScriptContextBuilder WithOutputResponse(Dictionary<string, object?> outputResponse)
    {
        _outputResponse = outputResponse;
        return this;
    }

    public IScriptContextBuilder WithMetadata(Dictionary<string, object> metadata)
    {
        _metadata = metadata;
        return this;
    }

    public IScriptContextBuilder WithDefinitions(Dictionary<string, object> definitions)
    {
        _definitions = definitions;
        return this;
    }

    public IScriptContextBuilder WithCurrentTransition(InstanceTransition? instanceTransition)
    {
        _instanceTransition = instanceTransition;
        return this;
    }

    public async Task<ScriptContext> BuildAsync(CancellationToken cancellationToken = default)
    {
        // Resolve workflow if needed
        var workflow = await ResolveWorkflowAsync(cancellationToken);

        // Resolve instance if needed
        var instance = await ResolveInstanceAsync(cancellationToken);

        // Resolve transition if needed
        var transition = ResolveTransition(workflow);

        var scriptTransitionRequest = BuildScriptTransitionRequest();

        // Build the ScriptContext using the domain builder
        return new ScriptContext.Builder(logger)
            .SetRuntime(_runtimeInfoProvider!)
            .SetWorkflow(workflow)
            .SetInstance(instance)
            .SetTransition(transition)
            .SetBody(_body)
            .SetHeaders(_headers)
            .SetRouteValues(_routeValues)
            .SetQueryParameters(_queryParameters)
            .SetTaskResponse(_taskResponse)
            .SetOutputResponse(_outputResponse)
            .SetMetadata(_metadata)
            .SetDefinitions(_definitions)
            .SetCurrentTransition(scriptTransitionRequest)
            .Build();
    }

    /// <summary>
    /// Builds ScriptTransitionRequest from persisted InstanceTransition when available.
    /// Header keys are normalized to lowercase.
    /// </summary>
    private ScriptTransitionRequest? BuildScriptTransitionRequest()
    {
        if (_instanceTransition == null)
            return null;

        var data = _instanceTransition.Body.JsonElement.ToDynamic();
        var header = ToHeaderDynamic(_instanceTransition.Header.JsonElement);
        return new ScriptTransitionRequest(data, header);
    }

    /// <summary>
    /// Converts header JsonElement to dynamic (ExpandoObject) with all keys normalized to lowercase.
    /// </summary>
    private static dynamic? ToHeaderDynamic(JsonElement headerElement)
    {
        if (headerElement.ValueKind != JsonValueKind.Object)
            return headerElement.ToDynamic();

        var headerExpando = new ExpandoObject() as IDictionary<string, object?>;
        foreach (var property in headerElement.EnumerateObject())
            headerExpando[property.Name.ToLowerInvariant()] = property.Value.ToDynamic();
        return headerExpando;
    }

    private async Task<Definitions.Workflow> ResolveWorkflowAsync(CancellationToken cancellationToken)
    {
        if (_workflow != null)
            return _workflow;

        if (_workflowReference != null)
        {
            var result = await componentCacheStore.GetFlowAsync(_workflowReference, cancellationToken);
            return result.GetValueOrThrow();
        }

        if (_workflowDomain != null && _workflowKey != null)
        {
            var result = await componentCacheStore.GetFlowAsync(_workflowDomain, _workflowKey, _workflowVersion,
                cancellationToken);
            return result.GetValueOrThrow();
        }

        throw new InvalidOperationException("Workflow must be set either directly or through domain/key parameters.");
    }

    private async Task<Instance> ResolveInstanceAsync(CancellationToken cancellationToken)
    {
        if (_instance != null)
            return _instance;

        if (_instanceId.HasValue)
        {
            var instance = _noTracking
                ? await instanceRepository.FindByIdentifierAsReadOnlyAsync(_instanceId.Value.ToString(), cancellationToken)
                : await instanceRepository.FindByIdentifierAsync(_instanceId.Value.ToString(),
                    cancellationToken);
            
            if (instance == null)
                throw new InvalidOperationException($"Instance with ID {_instanceId.Value} not found.");
            
            _instance = instance.CreateSnapshot();
            return _instance;
        }

        throw new InvalidOperationException("Instance must be set either directly or through instance ID.");
    }

    private Transition? ResolveTransition(Definitions.Workflow workflow)
    {
        if (_transition != null)
            return _transition;

        if (!string.IsNullOrEmpty(_transitionKey))
        {
            var transition = workflow.FindTransitionInContext(_transitionKey);
            if (transition == null)
                throw new InvalidOperationException(
                    $"Transition with key '{_transitionKey}' not found in workflow '{workflow.Key}'.");
            return transition;
        }

        return null;
    }
}
