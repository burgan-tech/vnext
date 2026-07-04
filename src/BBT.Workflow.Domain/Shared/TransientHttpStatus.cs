using System.Net;

namespace BBT.Workflow.Shared;

/// <summary>
/// Shared transient-HTTP-status contract for service-to-service relays and invocations.
/// A transient status means the same request may succeed on redelivery, so at-least-once
/// relays (e.g. the Inbox orchestration forwarder) rethrow to trigger redelivery.
/// Non-transient statuses are permanent: redelivering them would poison the queue.
/// </summary>
public static class TransientHttpStatus
{
    /// <summary>
    /// Determines whether an HTTP status is transient and therefore worth redelivering:
    /// any server error (>= 500), 408 Request Timeout and 429 Too Many Requests.
    /// A plain 500 is indistinguishable from a temporary infrastructure failure
    /// (e.g. a DB blip surfacing as an unhandled exception), so it is treated as
    /// transient; redelivery is bounded by the inbox retry cap and ends in the
    /// dead-letter state instead of being silently dropped.
    /// </summary>
    /// <param name="status">The HTTP status code returned by the downstream service.</param>
    /// <returns><c>true</c> when the request should be redelivered; otherwise <c>false</c>.</returns>
    public static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500
        || status == HttpStatusCode.RequestTimeout
        || status == HttpStatusCode.TooManyRequests;
}
