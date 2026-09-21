using System.Net.Http.Headers;

namespace BBT.Workflow.Execution.Core.Invocation;

/// <summary>
/// Shared helpers for the invocation cores that live in THIS assembly (<see cref="DaprServiceInvocation"/>,
/// <see cref="SoapInvocation"/>). Both used to keep byte-identical private copies of
/// <see cref="MergeHeaders"/> justified by "the Execution host assembly is unreachable" — true of
/// the host's own <c>InvokerHelpers</c>, but irrelevant to each other since both cores already live
/// in this same <c>Execution.Core</c> assembly. Extracted here instead.
/// <para>
/// Deliberately does NOT include <c>HttpTaskInvocation</c>'s copy in
/// <c>BBT.Workflow.Execution.Abstractions</c> — that really is a different assembly this project
/// cannot lean on for a contracts-only package, so that copy's own justification still holds.
/// </para>
/// </summary>
internal static class InvocationHelpers
{
    /// <summary>
    /// Merges response headers and content headers into a single dictionary.
    /// Uses case-insensitive key comparison and concatenates duplicate header values.
    /// </summary>
    public static Dictionary<string, string> MergeHeaders(
        HttpResponseHeaders responseHeaders,
        HttpContentHeaders contentHeaders)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in responseHeaders.Concat(contentHeaders))
        {
            var value = string.Join(", ", header.Value);
            result[header.Key] = result.TryGetValue(header.Key, out var existing)
                ? $"{existing}, {value}"
                : value;
        }

        return result;
    }
}
