using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace BBT.Workflow.Orchestration.Controllers.Instances;

/// <summary>
/// Detects whether an incoming request body should be treated as a standard vNext payload
/// (containing <c>attributes</c>, <c>key</c>, <c>tags</c> etc.) or as a free-form payload
/// that must be normalized by wrapping the entire body under <c>attributes</c>.
/// </summary>
internal static class PayloadModeDetector
{
    /// <summary>Header name that explicitly overrides auto-detection.</summary>
    private const string HeaderName = "x-vnext-payload-mode";

    /// <summary>
    /// Returns <c>true</c> when the body should be treated as a standard payload,
    /// <c>false</c> when it is a free-form payload that needs normalization.
    /// </summary>
    /// <remarks>
    /// Resolution order:
    /// <list type="number">
    ///   <item><c>x-vnext-payload-mode: standard</c> → standard</item>
    ///   <item><c>x-vnext-payload-mode: raw</c> → free-form</item>
    ///   <item>Body contains top-level <c>attributes</c> property → standard</item>
    ///   <item>Body absent or no <c>attributes</c> property → free-form</item>
    /// </list>
    /// </remarks>
    internal static bool IsStandard(IHeaderDictionary headers, JsonElement? body)
    {
        if (headers.TryGetValue(HeaderName, out var mode))
            return !string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase);

        if (body is null || body.Value.ValueKind != JsonValueKind.Object)
            return true;

        return body.Value.TryGetProperty("attributes", out _);
    }
}
