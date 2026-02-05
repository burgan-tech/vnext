using System;
using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Infrastructure.Instances.Remote;

/// <summary>
/// Remote implementation of instance retry operations using HTTP client calls to InstanceController.
/// Uses IDomainDiscoveryResolver to dynamically resolve endpoint URLs based on target domain.
/// </summary>
public sealed class RemoteInstanceRetryAppService(
    HttpClient httpClient,
    IOptions<RemoteOptions> options,
    IDomainDiscoveryResolver endpointResolver)
    : IRemoteInstanceRetryAppService
{
    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Retries a faulted workflow instance by calling the remote API
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/retry
    /// </summary>
    public async Task<Result<RetryInstanceOutput>> RetryAsync(
        RetryInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            if (!endpointResult.IsSuccess)
            {
                return Result<RetryInstanceOutput>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.Retry(input.Domain, input.Workflow, input.Instance, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(input.Version))
                queryParams.Add($"version={Uri.EscapeDataString(input.Version)}");
            if (input.Sync)
                queryParams.Add("sync=true");

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            // Build request body from data if provided
            var content = input.Data != null
                ? new StringContent(JsonSerializer.Serialize(input.Data, JsonOptions), Encoding.UTF8, "application/json")
                : new StringContent("{}", Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };

            // Forward headers
            foreach (var header in input.Headers)
            {
                if (!IsRestrictedHeader(header.Key))
                {
                    requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleResponseAsync<RetryInstanceOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return Result<RetryInstanceOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
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
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<T>(responseContent, JsonOptions);
            return Result<T>.Ok(result!);
        }

        // Map status codes to appropriate Error types (per Railway Pattern)
        return await MapStatusCodeToError<T>(response, cancellationToken);
    }

    /// <summary>
    /// Maps HTTP status codes to appropriate Error types following Railway Pattern guidelines.
    /// </summary>
    private static async Task<Result<T>> MapStatusCodeToError<T>(HttpResponseMessage response,
        CancellationToken cancellationToken)
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
                    var error = prefix switch
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
                    return Result<T>.Fail(error);
                }
            }
            catch (JsonException)
            {
                // If JSON deserialization fails, fall back to status code mapping
            }
        }

        // Map status code to appropriate Error type (Railway Pattern best practice)
        var mappedError = statusCode switch
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

        return Result<T>.Fail(mappedError);
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

/// <summary>
/// DTO for deserializing error responses from remote services
/// </summary>
internal sealed class ServiceErrorResponse
{
    public ServiceError? Error { get; set; }
}

/// <summary>
/// DTO for service error details
/// </summary>
internal sealed class ServiceError
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? Prefix { get; set; }
    public string? Target { get; set; }
    public string? Details { get; set; }
}
