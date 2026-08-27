using System.Text.Json;
using BBT.Workflow.Payloads;

namespace BBT.Workflow.Orchestration.Controllers.Instances;

/// <summary>
/// Detects whether an incoming request body should be treated as a standard vNext payload
/// (containing <c>attributes</c>, <c>key</c>, <c>tags</c> etc.) or as a free-form payload
/// that must be normalized by wrapping the entire body under <c>attributes</c>.
/// </summary>
internal static class PayloadModeDetector
{
    /// <summary>
    /// Returns <c>true</c> when the body should be treated as a standard payload,
    /// <c>false</c> when it is a free-form payload that needs normalization.
    /// </summary>
    /// <remarks>
    /// Resolution order:
    /// <list type="number">
    ///   <item><c>x-vnext-payload-mode: standard</c> → standard</item>
    ///   <item><c>x-vnext-payload-mode: raw</c> → free-form</item>
    ///   <item>Body absent or not a JSON object → standard</item>
    ///   <item>Otherwise the envelope vocabulary decides — see
    ///         <see cref="PayloadEnvelope.IsStandardShape"/>.</item>
    /// </list>
    /// Getting this wrong is not cosmetic: a free-form classification wraps the whole body under
    /// <c>attributes</c>, so the transition/start schema would be evaluated against the envelope
    /// fields and reject a valid request.
    /// </remarks>
    internal static bool IsStandard(IHeaderDictionary headers, JsonElement? body)
    {
        var fromHeader = headers.TryGetValue(PayloadEnvelope.ModeHeaderName, out var mode)
            ? PayloadEnvelope.ResolveModeFromHeader(mode.ToString())
            : null;

        if (fromHeader.HasValue)
            return fromHeader.Value;

        if (body is null || body.Value.ValueKind != JsonValueKind.Object)
            return true;

        return PayloadEnvelope.IsStandardShape(
            body.Value.EnumerateObject().Select(property => property.Name));
    }
}
