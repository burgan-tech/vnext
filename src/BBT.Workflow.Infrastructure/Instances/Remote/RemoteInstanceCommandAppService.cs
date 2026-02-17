using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
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
    IDomainDiscoveryResolver endpointResolver)
    : IRemoteInstanceCommandAppService
{
    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

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

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            var requestBody = new CreateInstanceInput
            {
                Key = input.Instance.Key,
                Tags = input.Instance.Tags,
                Attributes = input.Instance.Attributes
            };

            var jsonContent = JsonSerializer.Serialize(requestBody, JsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };

            if (input.Headers != null)
                foreach (var header in input.Headers)
                {
                    if (!IsRestrictedHeader(header.Key))
                    {
                        requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

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

            var jsonContent = JsonSerializer.Serialize(requestBody, JsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };

            if (input.Headers != null)
                foreach (var header in input.Headers)
                {
                    if (!IsRestrictedHeader(header.Key))
                    {
                        requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

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
            if (!string.IsNullOrEmpty(input.Version))
                queryParams.Add($"version={Uri.EscapeDataString(input.Version)}");
            if (input.Sync)
                queryParams.Add("sync=true");

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            var content = input.Data != null
                ? new StringContent(JsonSerializer.Serialize(input.Data, JsonOptions), Encoding.UTF8,
                    "application/json")
                : new StringContent("{}", Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Patch, requestUri)
            {
                Content = content
            };

            foreach (var header in input.Headers)
            {
                if (!IsRestrictedHeader(header.Key))
                {
                    requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

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

            var jsonContent = JsonSerializer.Serialize(input, JsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };

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

            var jsonContent = JsonSerializer.Serialize(input, JsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };

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
            var result = JsonSerializer.Deserialize<T>(responseContent, JsonOptions);
            return Result<T>.Ok(result!);
        }

        // Map status codes to appropriate Error types (per Railway Pattern)
        return await MapStatusCodeToError<T>(response, cancellationToken);
    }

    /// <summary>
    /// Non-generic overload for void operations
    /// </summary>
    private static async Task<Result> HandleResponseAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // Success case
        if (response.IsSuccessStatusCode)
        {
            return Result.Ok();
        }

        // Map status codes to appropriate Error types
        var error = await MapStatusCodeToErrorCore(response, cancellationToken);
        return Result.Fail(error);
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
    private static async Task<Result<T>> MapStatusCodeToError<T>(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var error = await MapStatusCodeToErrorCore(response, cancellationToken);
        return Result<T>.Fail(error);
    }

    private static async Task<Error> MapStatusCodeToErrorCore(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var errorContent = await response.ReadDecompressedContentAsync(cancellationToken);
        var statusCode = response.StatusCode;

        // Check if response has Aether error format header
        if (response.Headers.TryGetValues("_aether_error_format", out var values) &&
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
                        ErrorCodes.Prefixes.Validation => Error.Validation(code, errorResponse.Error.Message,
                            errorResponse.Error.Target),
                        ErrorCodes.Prefixes.NotFound => Error.NotFound(code, errorResponse.Error.Message,
                            errorResponse.Error.Target),
                        ErrorCodes.Prefixes.Conflict => Error.Conflict(code, errorResponse.Error.Message,
                            errorResponse.Error.Target),
                        ErrorCodes.Prefixes.Unauthorized => Error.Unauthorized(code, errorResponse.Error.Message),
                        ErrorCodes.Prefixes.Forbidden => Error.Forbidden(code, errorResponse.Error.Message),
                        ErrorCodes.Prefixes.Dependency => Error.Dependency(code, errorResponse.Error.Message,
                            errorResponse.Error.Target),
                        ErrorCodes.Prefixes.Transient => Error.Transient(code, errorResponse.Error.Message,
                            errorResponse.Error.Target),
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

    /// <summary>
    /// Checks if a header is restricted and cannot be set manually
    /// </summary>
    private static bool IsRestrictedHeader(string headerName)
    {
        return headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase);
    }
}