using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Fluent builder interface for constructing ScriptContext instances with various configuration options.
/// This interface provides a clean, discoverable API for setting up ScriptContext with different data sources.
/// </summary>
public interface IScriptContextBuilder
{
    /// <summary>
    /// Sets the runtime information provider for the ScriptContext.
    /// </summary>
    IScriptContextBuilder WithRuntime(IRuntimeInfoProvider runtimeInfoProvider);
    
    /// <summary>
    /// Sets the workflow by retrieving it from cache using domain, workflow key, and version.
    /// </summary>
    IScriptContextBuilder WithWorkflow(string domain, string workflowKey, string? version = null);
    
    /// <summary>
    /// Sets the workflow directly from an existing Workflow instance.
    /// </summary>
    IScriptContextBuilder WithWorkflow(Definitions.Workflow? workflow);
    
    /// <summary>
    /// Sets the workflow by retrieving it from cache using a reference input.
    /// </summary>
    IScriptContextBuilder WithWorkflow(IReference workflowReference);
    
    /// <summary>
    /// Sets the instance by retrieving it from repository using instance ID.
    /// </summary>
    IScriptContextBuilder WithInstance(Guid instanceId, bool noTracking = false);
    
    /// <summary>
    /// Sets the instance directly from an existing Instance object.
    /// </summary>
    IScriptContextBuilder WithInstance(Instance? instance);
    
    /// <summary>
    /// Sets the transition by finding it in the workflow using transition key.
    /// </summary>
    IScriptContextBuilder WithTransition(string? transitionKey);
    
    /// <summary>
    /// Sets the transition directly from an existing Transition object.
    /// </summary>
    IScriptContextBuilder WithTransition(Transition? transition);
    
    /// <summary>
    /// Sets the request body data for the ScriptContext.
    /// </summary>
    IScriptContextBuilder WithBody(object? body);
    
    /// <summary>
    /// Sets the request headers for the ScriptContext.
    /// </summary>
    IScriptContextBuilder WithHeaders(Dictionary<string, string?>? headers);
    
    /// <summary>
    /// Sets the route values for the ScriptContext.
    /// </summary>
    IScriptContextBuilder WithRouteValues(Dictionary<string, object?>? routeValues);
    
    /// <summary>
    /// Sets the route values for the ScriptContext.
    /// </summary>
    IScriptContextBuilder WithRouteValues(Dictionary<string, string?>? routeValues);
    
    /// <summary>
    /// Sets the query parameters for the ScriptContext.
    /// </summary>
    IScriptContextBuilder WithQueryParameters(Dictionary<string, object?>? queryParameters);
    
    /// <summary>
    /// Sets the query parameters for the ScriptContext.
    /// </summary>
    IScriptContextBuilder WithQueryParameters(Dictionary<string, string?>? queryParameters);
    
    /// <summary>
    /// Sets the task response data for the ScriptContext.
    /// </summary>
    IScriptContextBuilder WithTaskResponse(Dictionary<string, object?> taskResponse);
    
    /// <summary>
    /// Sets the metadata for the ScriptContext.
    /// </summary>
    IScriptContextBuilder WithMetadata(Dictionary<string, object> metadata);
    
    /// <summary>
    /// Sets the definitions for the ScriptContext.
    /// </summary>
    IScriptContextBuilder WithDefinitions(Dictionary<string, object> definitions);

    /// <summary>
    /// Sets the original transition request (body and headers) from the persisted InstanceTransition.
    /// When set, ScriptContext.CurrentTransition will expose this data with header keys normalized to lowercase.
    /// </summary>
    IScriptContextBuilder WithCurrentTransition(InstanceTransition? instanceTransition);

    /// <summary>
    /// Builds the ScriptContext asynchronously with all configured properties.
    /// This method retrieves any data that needs to be fetched asynchronously and constructs the final ScriptContext.
    /// </summary>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A fully configured ScriptContext instance.</returns>
    Task<ScriptContext> BuildAsync(CancellationToken cancellationToken = default);
}