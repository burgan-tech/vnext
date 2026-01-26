using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Instances.Remote;

/// <summary>
/// DTO for Aether error format from remote API responses
/// </summary>
internal sealed record ServiceErrorResponse
{
    public ServiceErrorInfo? Error { get; init; }
}

/// <summary>
/// Error information from Aether format
/// </summary>
internal sealed record ServiceErrorInfo
{
    public string? Prefix { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? Details { get; init; }
    public string? Target { get; init; }
}

/// <summary>
/// Remote implementation of instance query operations using HTTP client calls to InstanceController.
/// Uses IDomainDiscoveryResolver to dynamically resolve endpoint URLs based on target domain.
/// </summary>
public sealed class RemoteInstanceQueryAppService(
    HttpClient httpClient,
    IOptions<RemoteOptions> options,
    IDomainDiscoveryResolver endpointResolver)
    : IRemoteInstanceQueryAppService
{
    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

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
            var endpoint = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            var relativePath = InstanceUrlTemplates.Instance(input.Domain, input.Workflow, input.Instance, ApiVersionPrefix);

            var queryParams = new List<string>();
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
            var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);

            // Add If-None-Match header for ETag support
            if (!string.IsNullOrEmpty(input.IfNoneMatch))
            {
                requestMessage.Headers.TryAddWithoutValidation("If-None-Match", input.IfNoneMatch);
            }

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
            var endpoint = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            var relativePath = InstanceUrlTemplates.Data(input.Domain, input.Workflow, input.Instance, ApiVersionPrefix);

            var queryParams = new List<string>();
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
            var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);

            // Add If-None-Match header for ETag support
            if (!string.IsNullOrEmpty(input.IfNoneMatch))
            {
                requestMessage.Headers.TryAddWithoutValidation("If-None-Match", input.IfNoneMatch);
            }

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
            var endpoint = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

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

            if (input.Filter?.Length > 0)
            {
                foreach (var filter in input.Filter)
                {
                    queryParams.Add($"filter={Uri.EscapeDataString(filter)}");
                }
            }

            if (!string.IsNullOrWhiteSpace(input.GroupBy))
            {
                queryParams.Add($"groupBy={Uri.EscapeDataString(input.GroupBy)}");
            }

            if (!string.IsNullOrWhiteSpace(input.Aggregations))
            {
                queryParams.Add($"aggregations={Uri.EscapeDataString(input.Aggregations)}");
            }

            if (input.Extension?.Length > 0)
            {
                foreach (var ext in input.Extension)
                {
                    queryParams.Add($"extensions={Uri.EscapeDataString(ext)}");
                }
            }

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            var response = await httpClient.GetAsync(requestUri, cancellationToken);

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
            var endpoint = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            var relativePath = InstanceUrlTemplates.InstanceHistory(input.Domain, input.Workflow, input.Instance, ApiVersionPrefix);

            var queryParams = new List<string>();
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
            var response = await httpClient.GetAsync(requestUri, cancellationToken);

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
    public async Task<Result<GetInstanceStateOutput>> GetFunctionWithStateAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpoint = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

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

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            var response = await httpClient.GetAsync(requestUri, cancellationToken);

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleResponseAsync<GetInstanceStateOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return Result<GetInstanceStateOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Retrieves view function result for an instance (returns GetViewOutput with platform-specific content)
    /// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/functions/view
    /// </summary>
    public async Task<Result<GetViewOutput>> GetFunctionWithViewAsync(
        GetFunctionWithInstanceInput input,
        string? platform,
        string? transitionKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpoint = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            var relativePath = InstanceUrlTemplates.View(input.Domain, input.Workflow, input.Instance, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
            {
                queryParams.Add($"{nameof(input.Version).ToLowerInvariant()}={Uri.EscapeDataString(input.Version)}");
            }

            if (!string.IsNullOrEmpty(platform))
            {
                queryParams.Add($"{nameof(platform)}={Uri.EscapeDataString(platform)}");
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
            var response = await httpClient.GetAsync(requestUri, cancellationToken);

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
            var endpoint = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            var relativePath = InstanceUrlTemplates.Schema(input.Domain, input.Workflow, input.Instance, transitionKey, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
            {
                queryParams.Add($"{nameof(input.Version).ToLowerInvariant()}={Uri.EscapeDataString(input.Version)}");
            }

            if (queryParams.Count > 0)
                relativePath += "&" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            var response = await httpClient.GetAsync(requestUri, cancellationToken);

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
            var endpoint = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

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
            var response = await httpClient.GetAsync(requestUri, cancellationToken);

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
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<T>(responseContent, JsonOptions);
            return Result<T>.Ok(result!);
        }

        // Map status codes to appropriate Error types (per Railway Pattern)
        return await MapStatusCodeToError<T>(response, cancellationToken);
    }

    /// <summary>
    /// Handles conditional HTTP response (for ETag support)
    /// </summary>
    private static async Task<ConditionalResult<T>> HandleConditionalResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Success case
        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<T>(responseContent, JsonOptions);
            return ConditionalResult<T>.Success(result!);
        }

        // Map status codes to appropriate Error types
        var error = await MapStatusCodeToErrorCore(response, cancellationToken);
        return ConditionalResult<T>.Fail(error);
    }

    /// <summary>
    /// Maps HTTP status codes to appropriate Error types following Railway Pattern guidelines.
    /// - 400 Bad Request → Validation Error
    /// - 404 Not Found → NotFound Error
    /// - 409 Conflict → Conflict Error
    /// - 401 Unauthorized → Unauthorized Error
    /// - 403 Forbidden → Forbidden Error
    /// - 5xx Server Error → Dependency Error (external service failed)
    /// - Other → Dependency Error
    /// </summary>
    private static async Task<Result<T>> MapStatusCodeToError<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var error = await MapStatusCodeToErrorCore(response, cancellationToken);
        return Result<T>.Fail(error);
    }

    private static async Task<Error> MapStatusCodeToErrorCore(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var statusCode = response.StatusCode;

        // Check if response has Aether error format header
        if (response.Headers.TryGetValues("_bbt_error_format", out var values) &&
            values.Any(v => v.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var errorResponse = JsonSerializer.Deserialize<ServiceErrorResponse>(errorContent, JsonOptions);
                if (errorResponse?.Error != null)
                {
                    // Use the prefix from remote service if available, otherwise infer from status code
                    var prefix = errorResponse.Error.Prefix ?? InferPrefixFromStatusCode(statusCode);
                    var code = errorResponse.Error.Code ?? "remote_error";
                    
                    // Map to appropriate Error type based on prefix
                    return prefix switch
                    {
                        ErrorCodes.Prefixes.Validation => Error.Validation(code, errorResponse.Error.Message, errorResponse.Error.Target),
                        ErrorCodes.Prefixes.NotFound => Error.NotFound(code, errorResponse.Error.Message, errorResponse.Error.Target),
                        ErrorCodes.Prefixes.Conflict => Error.Conflict(code, errorResponse.Error.Message, errorResponse.Error.Target),
                        ErrorCodes.Prefixes.Unauthorized => Error.Unauthorized(code, errorResponse.Error.Message),
                        ErrorCodes.Prefixes.Forbidden => Error.Forbidden(code, errorResponse.Error.Message),
                        ErrorCodes.Prefixes.Dependency => Error.Dependency(code, errorResponse.Error.Message, errorResponse.Error.Target),
                        ErrorCodes.Prefixes.Transient => Error.Transient(code, errorResponse.Error.Message, errorResponse.Error.Target),
                        _ => Error.Failure(code, errorResponse.Error.Message, errorResponse.Error.Details)
                    };
                }
            }
            catch (JsonException)
            {
                // If JSON deserialization fails, fall back to status code mapping
            }
        }

        // Map status code to appropriate Error type (Railway Pattern best practice)
        return statusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => Error.Validation(
                "remote_bad_request",
                $"Remote API validation error: {errorContent}"),

            System.Net.HttpStatusCode.NotFound => Error.NotFound(
                "remote_not_found",
                "Requested resource not found on remote API",
                errorContent),

            System.Net.HttpStatusCode.Conflict => Error.Conflict(
                "remote_conflict",
                $"Remote API conflict: {errorContent}"),

            System.Net.HttpStatusCode.Unauthorized => Error.Unauthorized(
                "remote_unauthorized",
                "Unauthorized access to remote API"),

            System.Net.HttpStatusCode.Forbidden => Error.Forbidden(
                "remote_forbidden",
                "Forbidden access to remote API"),

            System.Net.HttpStatusCode.InternalServerError or
            System.Net.HttpStatusCode.BadGateway or
            System.Net.HttpStatusCode.ServiceUnavailable or
            System.Net.HttpStatusCode.GatewayTimeout => Error.Dependency(
                "remote_service_error",
                $"Remote API service error: {response.ReasonPhrase}",
                ((int)statusCode).ToString()),

            _ => Error.Dependency(
                "remote_http_error",
                $"Remote API returned HTTP {(int)statusCode}: {response.ReasonPhrase}",
                ((int)statusCode).ToString())
        };
    }

    /// <summary>
    /// Infers error prefix from HTTP status code for Aether error format
    /// </summary>
    private static string InferPrefixFromStatusCode(System.Net.HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => ErrorCodes.Prefixes.Validation,
            System.Net.HttpStatusCode.NotFound => ErrorCodes.Prefixes.NotFound,
            System.Net.HttpStatusCode.Conflict => ErrorCodes.Prefixes.Conflict,
            System.Net.HttpStatusCode.Unauthorized => ErrorCodes.Prefixes.Unauthorized,
            System.Net.HttpStatusCode.Forbidden => ErrorCodes.Prefixes.Forbidden,
            _ => ErrorCodes.Prefixes.Dependency
        };
    }
}