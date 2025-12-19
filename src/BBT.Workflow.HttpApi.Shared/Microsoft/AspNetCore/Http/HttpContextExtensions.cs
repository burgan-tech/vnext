using System.Text.RegularExpressions;

namespace Microsoft.AspNetCore.Http;

/// <summary>
/// Represents extracted HTTP request context for script binding.
/// </summary>
/// <param name="Headers">HTTP headers with normalized lowercase keys</param>
/// <param name="QueryParameters">Query parameters from the request</param>
public sealed record RequestBindingContext(
    Dictionary<string, string?> Headers,
    Dictionary<string, string?> QueryParameters);

/// <summary>
/// Extensions for HttpContext to extract Workflow-specific information from headers
/// </summary>
public static partial class HttpContextExtensions
{
    /// <summary>
    /// Extracts headers and query parameters from the HTTP context for script binding.
    /// Headers are normalized to lowercase keys using invariant culture.
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <returns>A tuple containing filtered headers and query parameters</returns>
    public static RequestBindingContext GetRequestBindingContext(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var header in context.Request.Headers)
        {
            var key = header.Key.ToLowerInvariant();
            
            // Use case-insensitive dictionary, so duplicates by case are handled automatically
            // Take first value if not already present (handles case collision)
            if (!headers.ContainsKey(key))
            {
                headers[key] = header.Value.FirstOrDefault();
            }
        }

        var queryParams = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var param in context.Request.Query)
        {
            var key = param.Key;
            
            // Handle potential key collisions
            if (!queryParams.ContainsKey(key))
            {
                queryParams[key] = param.Value.FirstOrDefault();
            }
        }

        return new RequestBindingContext(headers, queryParams);
    }

    /// <summary>
    /// Extracts headers from the HTTP context for script binding.
    /// Headers are normalized to lowercase keys using invariant culture.
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <returns>Dictionary of filtered headers with normalized keys</returns>
    public static Dictionary<string, string?> GetFilteredHeaders(this HttpContext context)
    {
        return context.GetRequestBindingContext().Headers;
    }

    /// <summary>
    /// Extracts query parameters from the HTTP context.
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <returns>Dictionary of query parameters</returns>
    public static Dictionary<string, string?> GetQueryParameters(this HttpContext context)
    {
        return context.GetRequestBindingContext().QueryParameters;
    }

    // Regex patterns for parsing header values
    [GeneratedRegex(@"^([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}),([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$")]
    private static partial Regex DeviceInfoPattern();

    [GeneratedRegex(@"^([^,]+),([^,]+),([^,]+),([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$")]
    private static partial Regex WorkflowInfoPattern();

    [GeneratedRegex(@"^([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}),([^,]+),([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}),([^,]+)$")]
    private static partial Regex ActorInfoPattern();

    private static string? GetHeaderValue(HttpContext context, string headerName)
    {
        return context.Request.Headers.TryGetValue(headerName, out var value) ? value.ToString() : null;
    }

    /// <summary>
    /// Extracts device information from HTTP headers
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <returns>Device information if available</returns>
    public static DeviceInfo? GetDeviceInfo(this HttpContext context)
    {
        var headerValue = GetHeaderValue(context, DeviceInfo.Name);
        if (string.IsNullOrEmpty(headerValue)) return null;

        var match = DeviceInfoPattern().Match(headerValue);
        if (!match.Success) return null;

        return new DeviceInfo(
            deviceId: Guid.Parse(match.Groups[1].Value),
            installationId: Guid.Parse(match.Groups[2].Value)
        );
    }

    /// <summary>
    /// Extracts workflow information from HTTP headers
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <returns>Workflow information if available</returns>
    public static WorkflowInfo? GetWorkflowInfo(this HttpContext context)
    {
        var headerValue = GetHeaderValue(context, WorkflowInfo.Name);
        if (string.IsNullOrEmpty(headerValue)) return null;

        var match = WorkflowInfoPattern().Match(headerValue);
        if (!match.Success) return null;

        return new WorkflowInfo(
            domain: match.Groups[1].Value,
            workflow: match.Groups[2].Value,
            version: match.Groups[3].Value,
            instanceId: Guid.Parse(match.Groups[4].Value)
        );
    }

    /// <summary>
    /// Extracts actor information from HTTP headers
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <returns>Actor information if available</returns>
    public static ActorInfo? GetActorInfo(this HttpContext context)
    {
        var headerValue = GetHeaderValue(context, ActorInfo.Name);
        if (string.IsNullOrEmpty(headerValue)) return null;

        var match = ActorInfoPattern().Match(headerValue);
        if (!match.Success) return null;

        return new ActorInfo(
            userId: Guid.Parse(match.Groups[1].Value),
            userReference: match.Groups[2].Value,
            scopeId: Guid.Parse(match.Groups[3].Value),
            scopeReference: match.Groups[4].Value
        );
    }
} 