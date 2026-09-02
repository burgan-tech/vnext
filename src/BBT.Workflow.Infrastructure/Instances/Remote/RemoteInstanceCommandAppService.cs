using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Aether.Tracing;
using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Instances.Remote;

/// <summary>
/// Remote implementation of instance command operations using HTTP client calls to InstanceController.
/// Uses IDomainDiscoveryResolver to dynamically resolve endpoint URLs based on target domain.
/// </summary>
public sealed class RemoteInstanceCommandAppService(
    HttpClient httpClient,
    IOptions<RemoteOptions> options,
    IDomainDiscoveryResolver endpointResolver,
    ICurrentUser currentUser,
    ICorrelationIdProvider correlationIdProvider)
    : IRemoteInstanceCommandAppService
{
    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    /// <summary>
    /// Starts a new workflow instance by calling the remote API
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/start
    /// </summary>
    public async Task<Result<StartInstanceOutput>> StartAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return Result<StartInstanceOutput>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.Start(input.Domain, input.Workflow, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
                queryParams.Add($"version={Uri.EscapeDataString(input.Version)}");
            if (input.Sync)
                queryParams.Add($"sync={input.Sync}");
            
            if (input.Extensions?.Length > 0)
            {
                foreach (var ext in input.Extensions)
                {
                    queryParams.Add($"extensions={Uri.EscapeDataString(ext)}");
                }
            }

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            var requestBody = new CreateInstanceInput
            {
                Key = input.Instance.Key,
                Stage = input.Instance.Stage,
                Tags = input.Instance.Tags,
                Attributes = input.Instance.Attributes
            };

            var jsonContent = JsonSerializer.Serialize(requestBody, JsonSerializerConstants.JsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers, RemoteHttpResponseHelper.IsRestrictedHeader, correlationIdProvider.Get());

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleResponseAsync<StartInstanceOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return Result<StartInstanceOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Starts a new sub workflow instance by calling the remote API
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/sub/{workflow}/instances/start
    /// </summary>
    public async Task<Result<StartInstanceOutput>> StartSubAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return Result<StartInstanceOutput>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.StartSub(input.Domain, input.Workflow, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
                queryParams.Add($"version={Uri.EscapeDataString(input.Version)}");
            if (input.Sync)
                queryParams.Add($"sync={input.Sync}");

            queryParams.Add("strictIdempotency=true");
            
            if (input.Extensions?.Length > 0)
            {
                foreach (var ext in input.Extensions)
                {
                    queryParams.Add($"extensions={Uri.EscapeDataString(ext)}");
                }
            }

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            var episode = WorkflowTraceLane.Episode;
            var requestBody = new
            {
                Id = input.Instance.Id,
                Key = input.Instance.Key,
                Tags = input.Instance.Tags,
                Stage = input.Instance.Stage,
                Attributes = input.Instance.Attributes,
                Callback = input.Instance.Callback,
                ExtraProperties = input.Instance.ExtraProperties,
                EpisodeStartedAt = episode?.StartedAt,
                EpisodeTrigger = episode?.Trigger,
                EpisodeTransitionKey = episode?.TransitionKey
            };

            var jsonContent = JsonSerializer.Serialize(requestBody, JsonSerializerConstants.JsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers, RemoteHttpResponseHelper.IsRestrictedHeader, correlationIdProvider.Get());

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleResponseAsync<StartInstanceOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return Result<StartInstanceOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Relays a transition to an active SubFlow instance in another domain.
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/internal/subflow-forward
    /// <para>
    /// Deliberately not the public transition endpoint: the claim proving the accept already
    /// reserved this chain's Busy flag travels in the request BODY. The public endpoint copies
    /// caller headers unfiltered and serializes only the data element, so a claim routed through
    /// it would be forgeable by any client. This endpoint is internal-only (network isolation),
    /// like the related-data endpoints. Error contract matches <see cref="TransitionAsync"/>.
    /// </para>
    /// </summary>
    public async Task<Result<TransitionOutput>> ForwardTransitionAsync(
        Guid instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            if (!endpointResult.IsSuccess)
                return Result<TransitionOutput>.Fail(endpointResult.Error);

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.SubflowForward(
                input.Domain,
                input.Workflow,
                instanceId.ToString(),
                ApiVersionPrefix);

            relativePath += $"?transitionKey={Uri.EscapeDataString(transitionKey)}";

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            var forwardInput = new SubflowForwardInput
            {
                Attributes = input.Data?.Attributes,
                Key = input.Data?.Key,
                Tags = input.Data?.Tags,
                Stage = input.Data?.Stage,
                Sync = input.Sync,
                ChainReserved = input.ChainReserved,
                CorrelationId = input.CorrelationId,
                RouteValues = input.RouteValues,
                // Hand the subflow's lane across the domain boundary. The caller
                // (ForwardToSubflowJobHandler) has already opened the child lane, so Current is the
                // forward span the subflow's hops must anchor on. Body, not header: a public
                // endpoint must never let a caller inject a lane.
                TraceRoot = WorkflowTraceLane.Current,
                ParentTraceRoot = WorkflowTraceLane.ParentLane,
                // The activation episode crosses with the lane: the subflow's time-to-Active is
                // measured from the client's request to the parent, not from this relay hop.
                EpisodeStartedAt = WorkflowTraceLane.Episode?.StartedAt,
                EpisodeTrigger = WorkflowTraceLane.Episode?.Trigger,
                EpisodeTransitionKey = WorkflowTraceLane.Episode?.TransitionKey
            };

            var jsonContent = JsonSerializer.Serialize(forwardInput, JsonSerializerConstants.JsonOptions);
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
            };

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers, RemoteHttpResponseHelper.IsRestrictedHeader, correlationIdProvider.Get());

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            return await HandleResponseAsync<TransitionOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result<TransitionOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Executes a transition on an existing workflow instance
    /// PATCH {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/transitions/{transitionKey}
    /// </summary>
    public async Task<Result<TransitionOutput>> TransitionAsync(
        Guid instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return Result<TransitionOutput>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.Transition(input.Domain, input.Workflow, instanceId.ToString(),
                transitionKey, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (input.Sync)
                queryParams.Add("sync=true");
            
            if (input.Extensions?.Length > 0)
            {
                foreach (var ext in input.Extensions)
                {
                    queryParams.Add($"extensions={Uri.EscapeDataString(ext)}");
                }
            }

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            var requestBody = input.Data is null
                ? "{}"
                : JsonSerializer.Serialize(input.Data, JsonSerializerConstants.JsonOptions);
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Patch, requestUri)
            {
                Content = content
            };

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers, RemoteHttpResponseHelper.IsRestrictedHeader, correlationIdProvider.Get());

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleResponseAsync<TransitionOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return Result<TransitionOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Cancels a child subflow through the ignored internal endpoint.
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/child-cancel
    /// </summary>
    public async Task<Result> CancelChildAsync(
        Guid instanceId,
        string domain,
        string flow,
        ChildSubflowCancelInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointResult = await endpointResolver.GetEndpointAsync(domain, EndpointKind.Url, cancellationToken);
            if (!endpointResult.IsSuccess)
                return Result.Fail(endpointResult.Error);

            var relativePath = InstanceUrlTemplates.ChildCancel(
                domain,
                flow,
                instanceId.ToString(),
                ApiVersionPrefix);
            var requestUri = new Uri(endpointResult.Value!.BaseUrl, relativePath.TrimStart('/'));
            var jsonContent = JsonSerializer.Serialize(input, JsonSerializerConstants.JsonOptions);
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
            };

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(
                requestMessage,
                forwardHeaders,
                null,
                RemoteHttpResponseHelper.IsRestrictedHeader,
                correlationIdProvider.Get());

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);
            return await HandleResponseAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Completes a sub workflow instance by calling the remote API
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/complete
    /// </summary>
    public async Task<Result> CompleteAsync(
        FlowCompletedInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return Result.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.Complete(input.Domain, input.Flow, input.InstanceId.ToString(),
                ApiVersionPrefix);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            var jsonContent = JsonSerializer.Serialize(input, JsonSerializerConstants.JsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, null, RemoteHttpResponseHelper.IsRestrictedHeader, correlationIdProvider.Get());

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleResponseAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return Result.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Updates the parent instance with SubFlow's state change by calling the remote API
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/sub/state
    /// </summary>
    public async Task<Result> UpdateSubFlowStateAsync(
        SubFlowStateChangedInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return Result.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.SubFlowState(
                input.Domain, 
                input.Flow, 
                input.ParentInstanceId.ToString(),
                ApiVersionPrefix);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            var jsonContent = JsonSerializer.Serialize(input, JsonSerializerConstants.JsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, null, RemoteHttpResponseHelper.IsRestrictedHeader, correlationIdProvider.Get());

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleResponseAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return Result.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Propagates SubFlow fault to parent instance by calling the remote API.
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/sub/fault
    /// </summary>
    public async Task<Result> FaultAsync(
        SubFlowFaultedInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            if (!endpointResult.IsSuccess)
            {
                return Result.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.SubFlowFault(
                input.Domain,
                input.Flow,
                input.InstanceId.ToString(),
                ApiVersionPrefix);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            var jsonContent = JsonSerializer.Serialize(input, JsonSerializerConstants.JsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, null, RemoteHttpResponseHelper.IsRestrictedHeader, correlationIdProvider.Get());

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            return await HandleResponseAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Propagates a canceled SubItem outcome to its parent instance by calling the remote API.
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/sub/cancel
    /// </summary>
    public async Task<Result> CancelAsync(
        SubItemCanceledInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            if (!endpointResult.IsSuccess)
            {
                return Result.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;
            var relativePath = InstanceUrlTemplates.SubFlowCancel(
                input.Domain,
                input.Flow,
                input.InstanceId.ToString(),
                ApiVersionPrefix);
            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            var jsonContent = JsonSerializer.Serialize(input, JsonSerializerConstants.JsonOptions);
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
            };

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(
                requestMessage,
                forwardHeaders,
                null,
                RemoteHttpResponseHelper.IsRestrictedHeader,
                correlationIdProvider.Get());

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);
            return await HandleResponseAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Marks instance Busy recursively by calling the remote API.
    /// PUT {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/busy
    /// </summary>
    public async Task<Result> MarkBusyAsync(
        MarkBusyInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            if (!endpointResult.IsSuccess)
                return Result.Fail(endpointResult.Error);

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.MarkBusy(
                input.Domain,
                input.Workflow,
                input.InstanceId.ToString(),
                ApiVersionPrefix);

            if (!string.IsNullOrEmpty(input.Version))
                relativePath += $"?version={Uri.EscapeDataString(input.Version)}";

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            var requestMessage = new HttpRequestMessage(HttpMethod.Put, requestUri);

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, null, RemoteHttpResponseHelper.IsRestrictedHeader, correlationIdProvider.Get());

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            return await HandleResponseAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Releases a chain reserve on a remote instance by calling the internal-only remote API.
    /// PUT {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/internal/busy-release
    /// </summary>
    public async Task<Result> ReleaseBusyAsync(
        MarkBusyInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            if (!endpointResult.IsSuccess)
                return Result.Fail(endpointResult.Error);

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.ReleaseBusy(
                input.Domain,
                input.Workflow,
                input.InstanceId.ToString(),
                ApiVersionPrefix);

            if (!string.IsNullOrEmpty(input.Version))
                relativePath += $"?version={Uri.EscapeDataString(input.Version)}";

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            var requestMessage = new HttpRequestMessage(HttpMethod.Put, requestUri);

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, null, RemoteHttpResponseHelper.IsRestrictedHeader, correlationIdProvider.Get());

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            return await HandleResponseAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Acknowledges a long-poll termination signal on a remote instance by calling the remote API.
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/longpoll/ack
    /// </summary>
    public async Task<Result> AcknowledgeLongPollAsync(
        AcknowledgeLongPollInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            if (!endpointResult.IsSuccess)
                return Result.Fail(endpointResult.Error);

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.LongPollAck(
                input.Domain,
                input.Workflow,
                input.Instance,
                ApiVersionPrefix);

            var query = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
                query.Add($"version={Uri.EscapeDataString(input.Version)}");
            if (!string.IsNullOrEmpty(input.Role))
                query.Add($"role={Uri.EscapeDataString(input.Role)}");
            if (query.Count > 0)
                relativePath += "?" + string.Join("&", query);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri);

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, null, RemoteHttpResponseHelper.IsRestrictedHeader, correlationIdProvider.Get());

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            return await HandleResponseAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Handles HTTP response by mapping status codes to appropriate Result types.
    /// Follows Railway Pattern: Status code → Result.Fail (not exceptions).
    /// </summary>
    private static async Task<Result<T>> HandleResponseAsync<T>(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // Success case
        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<T>(responseContent, JsonSerializerConstants.JsonOptions);
            return Result<T>.Ok(result!);
        }

        // Map status codes to appropriate Error types (per Railway Pattern)
        var error = await RemoteHttpResponseHelper.MapToErrorAsync(response, cancellationToken, JsonSerializerConstants.JsonOptions);
        return Result<T>.Fail(error);
    }

    /// <summary>
    /// Non-generic overload for void operations
    /// </summary>
    private static async Task<Result> HandleResponseAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return Result.Ok();

        var error = await RemoteHttpResponseHelper.MapToErrorAsync(response, cancellationToken, JsonSerializerConstants.JsonOptions);
        return Result.Fail(error);
    }
}
