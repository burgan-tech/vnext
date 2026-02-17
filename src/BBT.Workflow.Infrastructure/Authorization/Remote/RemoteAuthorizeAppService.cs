using System.Net;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Authorization.Remote;

/// <summary>
/// Remote implementation of authorize operations using HTTP client calls to the instance authorize function endpoint.
/// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/functions/authorize
/// Uses IDomainDiscoveryResolver to resolve endpoint by target domain.
/// </summary>
public sealed class RemoteAuthorizeAppService(
    HttpClient httpClient,
    IOptions<RemoteOptions> options,
    IDomainDiscoveryResolver endpointResolver)
    : IRemoteAuthorizeAppService
{
    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <inheritdoc />
    public async Task<Result<AuthorizeOutput>> GetAuthorizeResultForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string role,
        string? transitionKey,
        string? functionKey,
        string? version,
        bool checkQueryRoles,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointResult = await endpointResolver.GetEndpointAsync(domain, EndpointKind.Url, cancellationToken);
            if (!endpointResult.IsSuccess)
                return Result<AuthorizeOutput>.Fail(endpointResult.Error);

            var endpoint = endpointResult.Value!;
            var relativePath = InstanceUrlTemplates.Authorize(domain, workflow, instanceId, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(role))
                queryParams.Add($"role={Uri.EscapeDataString(role)}");
            if (!string.IsNullOrEmpty(transitionKey))
                queryParams.Add($"transitionKey={Uri.EscapeDataString(transitionKey)}");
            if (!string.IsNullOrEmpty(functionKey))
                queryParams.Add($"functionKey={Uri.EscapeDataString(functionKey)}");
            if (!string.IsNullOrEmpty(version))
                queryParams.Add($"version={Uri.EscapeDataString(version)}");
            if (checkQueryRoles)
                queryParams.Add("queryRoles=true");

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            var response = await httpClient.GetAsync(requestUri, cancellationToken);

            // 200 and 403 both return AuthorizeOutput body (allowed true/false)
            if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Forbidden)
            {
                var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
                var output = JsonSerializer.Deserialize<AuthorizeOutput>(responseContent, JsonOptions);
                return Result<AuthorizeOutput>.Ok(output ?? new AuthorizeOutput { Allowed = false });
            }

            var error = await MapStatusCodeToErrorCore(response, cancellationToken);
            return Result<AuthorizeOutput>.Fail(error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result<AuthorizeOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <inheritdoc />
    public async Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string? version,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointResult = await endpointResolver.GetEndpointAsync(domain, EndpointKind.Url, cancellationToken);
            if (!endpointResult.IsSuccess)
                return Result<AuthorizationMatrixOutput>.Fail(endpointResult.Error);

            var endpoint = endpointResult.Value!;
            var relativePath = InstanceUrlTemplates.Permissions(domain, workflow, instanceId, ApiVersionPrefix);

            if (!string.IsNullOrEmpty(version))
                relativePath += "?version=" + Uri.EscapeDataString(version);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            var response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
                var output = JsonSerializer.Deserialize<AuthorizationMatrixOutput>(responseContent, JsonOptions);
                return Result<AuthorizationMatrixOutput>.Ok(output!);
            }

            var error = await MapStatusCodeToErrorCore(response, cancellationToken);
            return Result<AuthorizationMatrixOutput>.Fail(error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result<AuthorizationMatrixOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
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
                    var prefix = errorResponse.Error.Prefix ?? InferPrefixFromStatusCode(statusCode);
                    var code = errorResponse.Error.Code ?? "remote_error";

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

        return statusCode switch
        {
            HttpStatusCode.BadRequest => Error.Validation(
                "remote_bad_request",
                $"Remote API validation error: {errorContent}"),

            HttpStatusCode.NotFound => Error.NotFound(
                "remote_not_found",
                "Requested resource not found on remote API",
                errorContent),

            HttpStatusCode.Conflict => Error.Conflict(
                "remote_conflict",
                $"Remote API conflict: {errorContent}"),

            HttpStatusCode.Unauthorized => Error.Unauthorized(
                "remote_unauthorized",
                "Unauthorized access to remote API"),

            HttpStatusCode.Forbidden => Error.Forbidden(
                "remote_forbidden",
                "Forbidden access to remote API"),

            HttpStatusCode.InternalServerError or
                HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.GatewayTimeout => Error.Dependency(
                "remote_service_error",
                $"Remote API service error: {response.ReasonPhrase}",
                ((int)statusCode).ToString()),

            _ => Error.Dependency(
                "remote_http_error",
                $"Remote API returned HTTP {(int)statusCode}: {response.ReasonPhrase}",
                ((int)statusCode).ToString())
        };
    }

    private static string InferPrefixFromStatusCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => ErrorCodes.Prefixes.Validation,
            HttpStatusCode.NotFound => ErrorCodes.Prefixes.NotFound,
            HttpStatusCode.Conflict => ErrorCodes.Prefixes.Conflict,
            HttpStatusCode.Unauthorized => ErrorCodes.Prefixes.Unauthorized,
            HttpStatusCode.Forbidden => ErrorCodes.Prefixes.Forbidden,
            _ => ErrorCodes.Prefixes.Dependency
        };
    }
}
