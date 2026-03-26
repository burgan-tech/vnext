using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Instances.Remote;

/// <summary>
/// Remote implementation of instance query operations using HTTP client calls to InstanceController.
/// Uses IDomainDiscoveryResolver to dynamically resolve endpoint URLs based on target domain.
/// </summary>
public sealed class RemoteInstanceQueryAppService(
    HttpClient httpClient,
    IOptions<RemoteOptions> options,
    IDomainDiscoveryResolver endpointResolver,
    ICurrentUser currentUser)
    : IRemoteInstanceQueryAppService
{
    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    /// <summary>
    /// Retrieves a single instance with optional extensions
    /// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}
    /// </summary>
    public async Task<ConditionalResult<GetInstanceOutput>> GetInstanceAsync(
        GetInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return ConditionalResult<GetInstanceOutput>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.Instance(input.Domain, input.Workflow, input.Instance, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
            {
                queryParams.Add($"version={Uri.EscapeDataString(input.Version)}");
            }

            if (input.Extensions?.Length > 0)
            {
                foreach (var ext in input.Extensions)
                {
                    queryParams.Add($"{nameof(input.Extensions).ToLowerInvariant()}={Uri.EscapeDataString(ext)}");
                }
            }

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);

            // Add If-None-Match header for ETag support
            if (!string.IsNullOrEmpty(input.IfNoneMatch))
            {
                requestMessage.Headers.TryAddWithoutValidation("If-None-Match", input.IfNoneMatch);
            }

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers);

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Handle 304 Not Modified - special case for conditional requests
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                return ConditionalResult<GetInstanceOutput>.NotModified();
            }

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleConditionalResponseAsync<GetInstanceOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return ConditionalResult<GetInstanceOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Retrieves only the instance data (attributes) with optional ETag support and extensions
    /// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/data
    /// </summary>
    public async Task<ConditionalResult<GetInstanceDataOutput>> GetInstanceDataAsync(
        GetInstanceDataInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return ConditionalResult<GetInstanceDataOutput>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.Data(input.Domain, input.Workflow, input.Instance, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
            {
                queryParams.Add($"version={Uri.EscapeDataString(input.Version)}");
            }

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
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);

            // Add If-None-Match header for ETag support
            if (!string.IsNullOrEmpty(input.IfNoneMatch))
            {
                requestMessage.Headers.TryAddWithoutValidation("If-None-Match", input.IfNoneMatch);
            }

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers);

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Handle 304 Not Modified - special case for conditional requests
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                return ConditionalResult<GetInstanceDataOutput>.NotModified();
            }

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleConditionalResponseAsync<GetInstanceDataOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return ConditionalResult<GetInstanceDataOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Retrieves a paginated list of instances with optional extensions
    /// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances
    /// </summary>
    public async Task<Result<InstanceListWithGroupsResponse<GetInstanceOutput>>> GetInstanceListAsync(
        GetInstanceListInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return Result<InstanceListWithGroupsResponse<GetInstanceOutput>>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.InstanceList(input.Domain, input.Workflow, ApiVersionPrefix);
            var queryParams = new List<string>
            {
                $"page={input.Page}",
                $"pageSize={input.PageSize}"
            };

            if (!string.IsNullOrWhiteSpace(input.Sort))
            {
                queryParams.Add($"sort={Uri.EscapeDataString(input.Sort)}");
            }

            if (!string.IsNullOrWhiteSpace(input.Filter))
            {
                queryParams.Add($"filter={Uri.EscapeDataString(input.Filter)}");
            }

            if (!string.IsNullOrWhiteSpace(input.GroupBy))
            {
                queryParams.Add($"groupBy={Uri.EscapeDataString(input.GroupBy)}");
            }

            if (!string.IsNullOrWhiteSpace(input.Aggregations))
            {
                queryParams.Add($"aggregations={Uri.EscapeDataString(input.Aggregations)}");
            }

            if (!string.IsNullOrEmpty(input.Version))
            {
                queryParams.Add($"version={Uri.EscapeDataString(input.Version)}");
            }

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
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);
            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers);
            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            return await HandleResponseAsync<InstanceListWithGroupsResponse<GetInstanceOutput>>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result<InstanceListWithGroupsResponse<GetInstanceOutput>>.Fail(
                Error.Transient("remote_network_error", ex.Message));
        }
    }
    
    /// <summary>
    /// Retrieves the complete history of an instance (all data transitions)
    /// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/transitions
    /// </summary>
    public async Task<Result<GetInstanceHistoryOutput>> GetInstanceHistoryAsync(
        GetInstanceHistoryInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return Result<GetInstanceHistoryOutput>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.InstanceHistory(input.Domain, input.Workflow, input.Instance, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
            {
                queryParams.Add($"version={Uri.EscapeDataString(input.Version)}");
            }

            if (input.Extensions?.Length > 0)
            {
                foreach (var ext in input.Extensions)
                {
                    queryParams.Add($"{nameof(input.Extensions).ToLowerInvariant()}={Uri.EscapeDataString(ext)}");
                }
            }

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);
            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers);
            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleResponseAsync<GetInstanceHistoryOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return Result<GetInstanceHistoryOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Retrieves function result for an instance (e.g., "state" function returns GetInstanceStateOutput)
    /// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/functions/{function}
    /// </summary>
    public async Task<ConditionalResult<GetInstanceStateOutput>> GetFunctionWithStateAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return Result<GetInstanceStateOutput>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.State(input.Domain, input.Workflow, input.Instance, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
            {
                queryParams.Add($"{nameof(input.Version).ToLowerInvariant()}={Uri.EscapeDataString(input.Version)}");
            }

            if (input.Extensions?.Length > 0)
            {
                foreach (var ext in input.Extensions)
                {
                    queryParams.Add($"{nameof(input.Extensions).ToLowerInvariant()}={Uri.EscapeDataString(ext)}");
                }
            }

            if (!string.IsNullOrEmpty(input.Role))
            {
                queryParams.Add($"role={Uri.EscapeDataString(input.Role)}");
            }

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);
            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers);
            if (!string.IsNullOrEmpty(input.IfNoneMatch))
                requestMessage.Headers.TryAddWithoutValidation("If-None-Match", input.IfNoneMatch);
            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                return ConditionalResult<GetInstanceStateOutput>.NotModified();

            var result = await HandleResponseAsync<GetInstanceStateOutput>(response, cancellationToken);
            return result.IsSuccess
                ? ConditionalResult<GetInstanceStateOutput>.Success(result.Value!)
                : ConditionalResult<GetInstanceStateOutput>.Fail(result.Error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return ConditionalResult<GetInstanceStateOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Retrieves view function result for an instance (returns GetViewOutput with platform-specific content)
    /// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/functions/view
    /// </summary>
    public async Task<Result<GetViewOutput>> GetFunctionWithViewAsync(
        GetFunctionWithInstanceInput input,
        string? transitionKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return Result<GetViewOutput>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.View(input.Domain, input.Workflow, input.Instance, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
            {
                queryParams.Add($"{nameof(input.Version).ToLowerInvariant()}={Uri.EscapeDataString(input.Version)}");
            }
            
            if (!string.IsNullOrEmpty(transitionKey))
            {
                queryParams.Add($"{nameof(transitionKey)}={Uri.EscapeDataString(transitionKey)}");
            }

            if (input.Extensions?.Length > 0)
            {
                foreach (var ext in input.Extensions)
                {
                    queryParams.Add($"{nameof(input.Extensions).ToLowerInvariant()}={Uri.EscapeDataString(ext)}");
                }
            }

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);
            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers);
            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleResponseAsync<GetViewOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return Result<GetViewOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Retrieves schema function result for an instance (returns GetSchemaOutput with transition schema)
    /// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/functions/schema?transitionKey={transitionKey}
    /// </summary>
    public async Task<Result<DTOs.GetSchemaOutput>> GetFunctionWithSchemaAsync(
        GetFunctionWithInstanceInput input,
        string transitionKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return Result<DTOs.GetSchemaOutput>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.Schema(input.Domain, input.Workflow, input.Instance, transitionKey, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
            {
                queryParams.Add($"{nameof(input.Version).ToLowerInvariant()}={Uri.EscapeDataString(input.Version)}");
            }

            if (queryParams.Count > 0)
                relativePath += "&" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);
            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers);
            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleResponseAsync<DTOs.GetSchemaOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return Result<DTOs.GetSchemaOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Retrieves extensions function result for an instance (returns GetExtensionsOutput with executed extension results)
    /// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/functions/extension?extensions={extensions}
    /// </summary>
    public async Task<Result<DTOs.GetExtensionsOutput>> GetFunctionWithExtensionsAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);
            
            if (!endpointResult.IsSuccess)
            {
                return Result<DTOs.GetExtensionsOutput>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.Extensions(input.Domain, input.Workflow, input.Instance, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
            {
                queryParams.Add($"{nameof(input.Version).ToLowerInvariant()}={Uri.EscapeDataString(input.Version)}");
            }

            if (input.Extensions?.Length > 0)
            {
                foreach (var ext in input.Extensions)
                {
                    queryParams.Add($"{nameof(input.Extensions).ToLowerInvariant()}={Uri.EscapeDataString(ext)}");
                }
            }

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);
            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers);
            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleResponseAsync<DTOs.GetExtensionsOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return Result<DTOs.GetExtensionsOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Handles HTTP response by mapping status codes to appropriate Result types.
    /// Follows Railway Pattern: Status code → Result.Fail (not exceptions).
    /// </summary>
    private static async Task<Result<T>> HandleResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Success case
        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<T>(responseContent, JsonSerializerConstants.JsonOptions);
            return Result<T>.Ok(result!);
        }

        var error = await RemoteHttpResponseHelper.MapToErrorAsync(response, cancellationToken, JsonSerializerConstants.JsonOptions);
        return Result<T>.Fail(error);
    }

    /// <summary>
    /// Handles conditional HTTP response (for ETag support)
    /// </summary>
    private static async Task<ConditionalResult<T>> HandleConditionalResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<T>(responseContent, JsonSerializerConstants.JsonOptions);
            return ConditionalResult<T>.Success(result!);
        }

        var error = await RemoteHttpResponseHelper.MapToErrorAsync(response, cancellationToken, JsonSerializerConstants.JsonOptions);
        return ConditionalResult<T>.Fail(error);
    }
}