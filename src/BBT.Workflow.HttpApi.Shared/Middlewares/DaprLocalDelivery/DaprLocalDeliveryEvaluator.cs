using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace BBT.Workflow.Middlewares;

/// <summary>Outcome of evaluating one inbound Dapr delivery.</summary>
public enum DaprLocalDeliveryVerdict
{
    /// <summary>Exactly one matching token and no caller app-id header.</summary>
    Accepted,

    /// <summary>No expected token is configured.</summary>
    TokenNotConfigured,

    /// <summary>The <c>dapr-api-token</c> header is absent.</summary>
    TokenMissing,

    /// <summary>The <c>dapr-api-token</c> header carries more than one value.</summary>
    TokenRepeated,

    /// <summary>The token does not equal the expected token.</summary>
    TokenInvalid,

    /// <summary>A <c>dapr-caller-app-id</c> header is present (service invocation, not a local delivery).</summary>
    CallerHeaderPresent
}

/// <summary>
/// Pure rule for "this request came from our own sidecar": one <c>dapr-api-token</c> equal to the
/// expected token, and no <c>dapr-caller-app-id</c> header. No trimming, no comma splitting. The
/// token value never appears in any output.
/// </summary>
public static class DaprLocalDeliveryEvaluator
{
    /// <summary>Header the sidecar uses to present the app API token.</summary>
    public const string TokenHeader = "dapr-api-token";

    /// <summary>Header Dapr adds on service invocation; never present on a local delivery.</summary>
    public const string CallerAppIdHeader = "dapr-caller-app-id";

    /// <summary>Evaluates the headers of an inbound request against the expected token.</summary>
    public static DaprLocalDeliveryVerdict Evaluate(IHeaderDictionary headers, string? expectedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            return DaprLocalDeliveryVerdict.TokenNotConfigured;
        }

        if (headers.ContainsKey(CallerAppIdHeader))
        {
            return DaprLocalDeliveryVerdict.CallerHeaderPresent;
        }

        if (!headers.TryGetValue(TokenHeader, out var values) || values.Count == 0)
        {
            return DaprLocalDeliveryVerdict.TokenMissing;
        }

        if (values.Count > 1)
        {
            return DaprLocalDeliveryVerdict.TokenRepeated;
        }

        var presented = Encoding.UTF8.GetBytes(values[0] ?? string.Empty);
        var expected = Encoding.UTF8.GetBytes(expectedToken);

        return CryptographicOperations.FixedTimeEquals(presented, expected)
            ? DaprLocalDeliveryVerdict.Accepted
            : DaprLocalDeliveryVerdict.TokenInvalid;
    }
}
