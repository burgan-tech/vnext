using System.Text;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.States;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
 
namespace BBT.Workflow.Tasks;
 
/// <summary>
/// Executes SignalR workflow tasks that send instance state updates to a SignalR hub.
/// This executor sends workflow instance state information to a configured SignalR endpoint.
/// </summary>
/// <param name="scriptEngine">The script engine used for compiling input/output mapping scripts.</param>
/// <param name="httpClientFactory">The HTTP client factory for creating HTTP clients to make web requests.</param>
/// <param name="runtimeInfoProvider">The runtime information provider for domain checks.</param>
/// <param name="instanceCorrelationRepository">The instance correlation repository for retrieving active correlations.</param>
/// <param name="stateMachineService">The state machine service for determining available transitions.</param>
/// <param name="configuration">The configuration for accessing SignalR URL settings.</param>
/// <param name="logger">The logger instance for logging SignalR task execution details.</param>
public sealed class SignalRTaskExecutor(
    IScriptEngine scriptEngine,
    IHttpClientFactory httpClientFactory,
    IRuntimeInfoProvider runtimeInfoProvider,
    IInstanceCorrelationRepository instanceCorrelationRepository,
    IStateMachineService stateMachineService,
    IConfiguration configuration,
    ILogger<SignalRTaskExecutor> logger) : TaskExecutor(scriptEngine, logger), ITaskExecutor
{
 
    /// <summary>
    /// Executes a SignalR task by building the instance state output and sending it to the configured SignalR hub.
    /// </summary>
    /// <param name="task">The SignalR workflow task containing configuration details.</param>
    /// <param name="scriptCode">The script code that handles input preparation and output processing.</param>
    /// <param name="context">The script context containing instance data and headers.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests during execution.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the processed response data
    /// from the SignalR hub call after output mapping transformation.
    /// </returns>
    /// <exception cref="HttpRequestException">Thrown when the HTTP request to SignalR hub fails.</exception>
    /// <exception cref="InvalidCastException">Thrown when the task cannot be cast to SignalRTask type.</exception>
    public async Task<object?> ExecuteAsync(
        WorkflowTask task,
        string scriptCode,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        var signalRTask = (task as SignalRTask)!;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        Logger.LogInformation("Starting SignalR task execution for task {TaskKey} - NotificationType: {NotificationType}",
            signalRTask.Key, signalRTask.NotificationType);
            
        StandardTaskResponse standardResponse;
 
        try
        {
            await PrepareInputAsync(signalRTask, scriptCode, context, cancellationToken);
            
            // Get SignalR URL from configuration
            var signalRUrl = configuration["SignalR:Url"];
            if (string.IsNullOrWhiteSpace(signalRUrl))
            {
                throw new InvalidOperationException("SignalR:Url configuration is missing or empty");
            }
 
            // Validate context has workflow and instance
            if (context.Workflow == null)
            {
                throw new InvalidOperationException("Workflow is required in context for SignalR task");
            }
 
            if (context.Instance == null)
            {
                throw new InvalidOperationException("Instance is required in context for SignalR task");
            }
 
            runtimeInfoProvider.Check(context.Runtime.Domain);
 
            // Get workflow and instance information
            var workflow = context.Workflow;
            var instance = context.Instance;
 
            // Build instance transition information
            var transitionInfo = await BuildInstanceTransitionInfoAsync(instance, cancellationToken);
 
            // Get available transitions
            var availableTransitions = GetMainFlowTransitions(instance, workflow, transitionInfo);
 
            // Build transition items with href links
            var transitionItems = availableTransitions.Select(transitionKey => new TransitionItem
            {
                Name = transitionKey,
                Href = string.Format(InstanceUrlTemplates.Transition, context.Runtime.Domain, workflow.Key, instance.Id, transitionKey)
            }).ToList();
 
            // Build data href
            var dataHref = new DataHref
            {
                Href = string.Format(InstanceUrlTemplates.Data, context.Runtime.Domain, workflow.Key, instance.Id)
            };
 
            // Build view href
            var viewHref = new ViewHref
            {
                Href = string.Format(InstanceUrlTemplates.View, context.Runtime.Domain, workflow.Key, instance.Id),
                LoadData = true
            };
 
            // Build active correlations with href links
            var activeCorrelations = transitionInfo.ActiveCorrelations.Select(correlation => new ActiveCorrelationHref
            {
                CorrelationId = correlation.CorrelationId,
                ParentState = correlation.ParentState,
                SubFlowInstanceId = correlation.SubFlowInstanceId,
                SubFlowType = correlation.SubFlowType,
                SubFlowDomain = correlation.SubFlowDomain,
                SubFlowName = correlation.SubFlowName,
                SubFlowVersion = correlation.SubFlowVersion,
                IsCompleted = correlation.IsCompleted,
                Href = string.Format(InstanceUrlTemplates.SubFlowData, correlation.SubFlowDomain, correlation.SubFlowName, correlation.SubFlowInstanceId)
            }).ToList();

            // Create instance output with or without additional data
            object instanceOutput;
            
            if (signalRTask.AdditionalData.HasValue)
            {
                var additionalData = signalRTask.AdditionalData.Value.Deserialize<object>();
                instanceOutput = new GetInstanceStateOutputWithAdditionalData
                {
                    Data = dataHref,
                    View = viewHref,
                    State = context.Instance.CurrentState ?? string.Empty,
                    Status = context.Instance.Status,
                    ActiveCorrelations = activeCorrelations,
                    Transitions = transitionItems,
                    ETag = context.Instance.LatestData?.ETag ?? string.Empty,
                    AdditionalData = additionalData
                };
                
                Logger.LogDebug("Created instance output with additional data for task {TaskKey}", signalRTask.Key);
            }
            else
            {
                instanceOutput = new GetInstanceStateOutput
                {
                    Data = dataHref,
                    View = viewHref,
                    State = context.Instance.CurrentState ?? string.Empty,
                    Status = context.Instance.Status,
                    ActiveCorrelations = activeCorrelations,
                    Transitions = transitionItems,
                    ETag = context.Instance.LatestData?.ETag ?? string.Empty
                };
            }
            
            var signalRRequest = new SignalRRequest
            {
                Id = context.Instance.Id.ToString(),
                Source = "vnext",
                Type = "vnext.workflow",
                Subject = "workflow-completed",
                Data = instanceOutput
            };
            
            // Create HTTP client
            var httpClient = CreateHttpClient();
            // Create HTTP request
            var request = new HttpRequestMessage(HttpMethod.Post, signalRUrl);
 
            // Add headers from context (only allowed headers)
            if (context.Headers != null && context.Headers.Count > 0 &&
                signalRTask.AllowedHeaders != null && signalRTask.AllowedHeaders.Count > 0)
            {
                var allowedHeadersSet = new HashSet<string>(signalRTask.AllowedHeaders, StringComparer.OrdinalIgnoreCase);
                var addedHeaderCount = 0;
                
                foreach (var header in context.Headers)
                {
                    var headerKey = (string)header.Key;
                    
                    // Only add header if it's in the allowed list
                    if (allowedHeadersSet.Contains(headerKey))
                    {
                        var headerValue = header.Value?.ToString() ?? string.Empty;
                        request.Headers.TryAddWithoutValidation(headerKey, headerValue);
                        addedHeaderCount++;
                        Logger.LogDebug("Added allowed header {HeaderKey}: {HeaderValue} to request", (object)headerKey, (object)headerValue);
                    }
                }
            }
            else if (signalRTask.AllowedHeaders == null || signalRTask.AllowedHeaders.Count == 0)
            {
                Logger.LogDebug("No allowed headers configured for SignalR task {TaskKey}, skipping header forwarding", signalRTask.Key);
            }
 
            // Serialize and add request body
            var requestContent = JsonSerializer.Serialize(signalRRequest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
            });
            
            if (string.IsNullOrWhiteSpace(requestContent))
            {
                Logger.LogWarning("SignalR request content is empty for task {TaskKey}", signalRTask.Key);
            }
            
            request.Content = new StringContent(
                requestContent,
                Encoding.UTF8,
                "application/json"
            );
 
            Logger.LogInformation("Sending SignalR request for task {TaskKey} to {SignalRUrl}",
                signalRTask.Key, signalRUrl);
                
            var response = await httpClient.SendAsync(request, cancellationToken);
            stopwatch.Stop();
 
            Logger.LogInformation("SignalR request completed for task {TaskKey} - Status: {StatusCode}, Duration: {Duration}ms",
                signalRTask.Key, response.StatusCode, stopwatch.ElapsedMilliseconds);
 
            // Extract response headers
            var responseHeaders = response.Headers
                .Concat(response.Content.Headers)
                .ToDictionary(h => h.Key.ToLower(), h => string.Join(", ", h.Value));
 
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            object? responseData = null;
            
            if (!string.IsNullOrWhiteSpace(content))
            {
                try
                {
                    responseData = JsonSerializer.Deserialize<object>(content);
                    Logger.LogDebug("Successfully deserialized response content to object for task {TaskKey}", signalRTask.Key);
                }
                catch (JsonException ex)
                {
                    Logger.LogWarning(ex, "Failed to deserialize response content as JSON for task {TaskKey}, treating as raw string", signalRTask.Key);
                    responseData = content;
                }
            }
 
            // Create standardized response based on HTTP status
            if (response.IsSuccessStatusCode)
            {
                Logger.LogInformation("SignalR task {TaskKey} completed successfully with status {StatusCode}",
                    signalRTask.Key, response.StatusCode);
                    
                standardResponse = CreateSuccessResponse(
                    data: responseData,
                    taskType: nameof(TaskType.SignalR),
                    executionDurationMs: stopwatch.ElapsedMilliseconds,
                    statusCode: (int)response.StatusCode,
                    headers: responseHeaders,
                    metadata: new Dictionary<string, object>
                    {
                        ["Url"] = signalRUrl,
                        ["NotificationType"] = signalRTask.NotificationType.ToString(),
                        ["InstanceId"] = instance.Id.ToString(),
                        ["ReasonPhrase"] = response.ReasonPhrase ?? string.Empty
                    });
            }
            else
            {
                Logger.LogWarning("SignalR task {TaskKey} returned non-success status {StatusCode}: {ReasonPhrase}",
                    signalRTask.Key, response.StatusCode, response.ReasonPhrase);
                    
                standardResponse = CreateErrorResponse(
                    errorMessage: $"SignalR request failed with status {response.StatusCode}: {response.ReasonPhrase}",
                    taskType: nameof(TaskType.SignalR),
                    executionDurationMs: stopwatch.ElapsedMilliseconds,
                    statusCode: (int)response.StatusCode,
                    metadata: new Dictionary<string, object>
                    {
                        ["Url"] = signalRUrl,
                        ["NotificationType"] = signalRTask.NotificationType.ToString(),
                        ["InstanceId"] = instance.Id.ToString(),
                        ["ReasonPhrase"] = response.ReasonPhrase ?? string.Empty,
                        ["ResponseContent"] = content
                    });
            }
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            Logger.LogWarning("SignalR task {TaskKey} was cancelled after {Duration}ms",
                signalRTask.Key, stopwatch.ElapsedMilliseconds);
                
            standardResponse = CreateErrorResponse(
                errorMessage: "SignalR request was cancelled",
                taskType: nameof(TaskType.SignalR),
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                exception: ex,
                metadata: new Dictionary<string, object>
                {
                    ["NotificationType"] = signalRTask.NotificationType.ToString(),
                    ["Cancelled"] = true
                });
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            Logger.LogError(ex, "SignalR request failed for task {TaskKey}, Duration: {Duration}ms",
                signalRTask.Key, stopwatch.ElapsedMilliseconds);
                
            standardResponse = CreateErrorResponse(
                errorMessage: ex.Message,
                taskType: nameof(TaskType.SignalR),
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                exception: ex,
                metadata: new Dictionary<string, object>
                {
                    ["NotificationType"] = signalRTask.NotificationType.ToString()
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.LogError(ex, "Unexpected error occurred during SignalR task {TaskKey} execution after {Duration}ms",
                signalRTask.Key, stopwatch.ElapsedMilliseconds);
                
            standardResponse = CreateErrorResponse(
                errorMessage: ex.Message,
                taskType: nameof(TaskType.SignalR),
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                exception: ex,
                metadata: new Dictionary<string, object>
                {
                    ["NotificationType"] = signalRTask.NotificationType.ToString()
                });
        }
 
        Logger.LogDebug("Setting standard response in context for SignalR task {TaskKey}", signalRTask.Key);
        context.SetStandardResponse(standardResponse);
        
        Logger.LogDebug("Processing output for SignalR task {TaskKey}", signalRTask.Key);
        var outputResponse = await ProcessOutputAsync(scriptCode, context, cancellationToken);
        
        Logger.LogInformation("SignalR task {TaskKey} execution completed, returning processed output", signalRTask.Key);
        return outputResponse;
    }
 
    /// <summary>
    /// Builds instance transition information including status, current state, and correlations.
    /// </summary>
    /// <param name="instance">The workflow instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A tuple containing status, current state, and correlations</returns>
    private async Task<(InstanceStatus Status, string? CurrentState, List<InstanceCorrelationInfo> ActiveCorrelations)>
        BuildInstanceTransitionInfoAsync(Instance instance, CancellationToken cancellationToken)
    {
        // Get active correlations from repository for reliability
        var correlations = await instanceCorrelationRepository.GetActiveByParentAsync(instance.Id, cancellationToken);
        
        var activeCorrelations = correlations
            .Select(c => new InstanceCorrelationInfo
            {
                CorrelationId = c.Id,
                ParentState = c.ParentState,
                SubFlowInstanceId = c.SubFlowInstanceId,
                SubFlowType = c.SubFlowType,
                SubFlowDomain = c.SubFlowDomain,
                SubFlowName = c.SubFlowName,
                SubFlowVersion = c.SubFlowVersion,
                IsCompleted = c.IsCompleted
            })
            .ToList();
 
        return (instance.Status, instance.CurrentState, activeCorrelations);
    }
 
    /// <summary>
    /// Gets available transitions from the main workflow instance.
    /// </summary>
    /// <param name="instance">The workflow instance</param>
    /// <param name="currentWorkflow">The current workflow definition</param>
    /// <param name="transitionInfo">Transition info</param>
    /// <returns>List of available transition keys</returns>
    private List<string> GetMainFlowTransitions(
        Instance instance,
        BBT.Workflow.Definitions.Workflow currentWorkflow,
        (InstanceStatus Status, string? CurrentState, List<InstanceCorrelationInfo> ActiveCorrelations) transitionInfo)
    {
        var availableTransitions = new List<string>();
 
        if (instance.Status.Equals(InstanceStatus.Active))
        {
            availableTransitions = stateMachineService.AvailableUserTransitionKeys(currentWorkflow, instance);
        }
 
        return availableTransitions;
    }
      private HttpClient CreateHttpClient()
    {
 
        var httpClient = httpClientFactory.CreateClient(HttpTaskExecutor.NoSslValidationHttpClientName);
            
        return httpClient;
    }
 
}