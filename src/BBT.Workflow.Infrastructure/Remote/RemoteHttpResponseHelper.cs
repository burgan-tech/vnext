using System.Net;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Domain;

namespace BBT.Workflow.Remote;

/// <summary>
/// Shared helper for remote HTTP client responses: maps status codes to Aether Errors (Railway Pattern)
/// and provides common utilities (e.g. restricted headers). Used by RemoteInstanceCommandAppService,
/// RemoteInstanceQueryAppService, RemoteInstanceRetryAppService, and RemoteAuthorizeAppService.
/// Each service keeps its own success handling (e.g. Authorize 200/403 as success, Query 304 NotModified).
/// </summary>
public static class RemoteHttpResponseHelper
{
    private static readonly JsonSerializerOptions DefaultErrorJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>
    /// Maps HTTP response to an Aether Error when the response is not successful.
    /// - Respects _aether_error_format header and deserializes remote error body when present.
    /// - Otherwise maps status code: 400→Validation, 404→NotFound, 409→Conflict, 401→Unauthorized,
    ///   403→Forbidden, 5xx→Dependency, other→Dependency.
    /// </summary>
    /// <param name="response">The non-success HTTP response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="jsonOptions">Optional JSON options for deserializing Aether error body; uses default if null.</param>
    /// <returns>The mapped <see cref="Error"/>.</returns>
    public static async Task<Error> MapToErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        JsonSerializerOptions? jsonOptions = null)
    {
        var errorContent = await response.ReadDecompressedContentAsync(cancellationToken);
        var statusCode = response.StatusCode;
        var options = jsonOptions ?? DefaultErrorJsonOptions;

        var aetherError = TryParseAetherError(response, errorContent, statusCode, options);
        if (aetherError is { } e)
            return e;

        return MapStatusCodeToError(statusCode, response.ReasonPhrase, errorContent);
    }

    /// <summary>
    /// Returns true if the header name is restricted and must not be set manually on outbound requests
    /// (e.g. Content-Type, Content-Length, Host, Connection). Use with CurrentUserForwardHeadersHelper.
    /// </summary>
    public static bool IsRestrictedHeader(string headerName)
    {
        return headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
               headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase);
    }

    private static Error? TryParseAetherError(
        HttpResponseMessage response,
        string errorContent,
        HttpStatusCode statusCode,
        JsonSerializerOptions options)
    {
        if (!response.Headers.TryGetValues("_aether_error_format", out var values) ||
            !values.Any(v => v.Equals("true", StringComparison.OrdinalIgnoreCase)))
            return null;

        try
        {
            var errorResponse = JsonSerializer.Deserialize<ServiceErrorResponse>(errorContent, options);
            if (errorResponse?.Error == null)
                return null;

            var prefix = errorResponse.Error.Prefix ?? InferPrefixFromStatusCode(statusCode);
            var code = errorResponse.Error.Code ?? "remote_error";

            return prefix switch
            {
                ErrorCodes.Prefixes.Validation => Error.Validation(code, errorResponse.Error.Message!, errorResponse.Error.Target),
                ErrorCodes.Prefixes.NotFound => Error.NotFound(code, errorResponse.Error.Message!, errorResponse.Error.Target),
                ErrorCodes.Prefixes.Conflict => Error.Conflict(code, errorResponse.Error.Message!, errorResponse.Error.Target),
                ErrorCodes.Prefixes.Unauthorized => Error.Unauthorized(code, errorResponse.Error.Message!),
                ErrorCodes.Prefixes.Forbidden => Error.Forbidden(code, errorResponse.Error.Message!),
                ErrorCodes.Prefixes.Dependency => Error.Dependency(code, errorResponse.Error.Message!, errorResponse.Error.Target),
                ErrorCodes.Prefixes.Transient => Error.Transient(code, errorResponse.Error.Message!, errorResponse.Error.Target),
                _ => Error.Failure(code, errorResponse.Error.Message!, errorResponse.Error.Details)
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Error MapStatusCodeToError(HttpStatusCode statusCode, string? reasonPhrase, string errorContent)
    {
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
                $"Remote API service error: {reasonPhrase}",
                ((int)statusCode).ToString()),

            _ => Error.Dependency(
                "remote_http_error",
                $"Remote API returned HTTP {(int)statusCode}: {reasonPhrase}",
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

/// <summary>
/// DTO for Aether error format from remote API responses. Used by <see cref="RemoteHttpResponseHelper"/>.
/// </summary>
internal sealed record ServiceErrorResponse
{
    public ServiceErrorInfo? Error { get; init; }
}

/// <summary>
/// Error details from Aether error format.
/// </summary>
internal sealed record ServiceErrorInfo
{
    public string? Prefix { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? Details { get; init; }
    public string? Target { get; init; }
}
