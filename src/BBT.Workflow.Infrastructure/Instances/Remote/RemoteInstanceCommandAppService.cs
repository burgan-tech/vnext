using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;
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
    ICurrentUser currentUser)
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
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers, RemoteHttpResponseHelper.IsRestrictedHeader);

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

            var requestBody = new CreateInstanceInput
            {
                Id = input.Instance.Id,
                Key = input.Instance.Key,
                Tags = input.Instance.Tags,
                Attributes = input.Instance.Attributes,
                Callback = input.Instance.Callback,
                ExtraProperties = input.Instance.ExtraProperties
            };

            var jsonContent = JsonSerializer.Serialize(requestBody, JsonSerializerConstants.JsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers, RemoteHttpResponseHelper.IsRestrictedHeader);

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

            var content = input.Data != null
                ? new StringContent(JsonSerializer.Serialize(input.Data, JsonSerializerConstants.JsonOptions), Encoding.UTF8,
                    "application/json")
                : new StringContent("{}", Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Patch, requestUri)
            {
                Content = content
            };

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers, RemoteHttpResponseHelper.IsRestrictedHeader);

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
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, null, RemoteHttpResponseHelper.IsRestrictedHeader);

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
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, null, RemoteHttpResponseHelper.IsRestrictedHeader);

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