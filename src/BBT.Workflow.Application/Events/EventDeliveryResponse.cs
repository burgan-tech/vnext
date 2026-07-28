using System.Text.Json.Serialization;

namespace BBT.Workflow.Events;

/// <summary>
/// Dapr pub/sub protocol signal values. Dapr reads the top-level <c>status</c> field of the
/// subscriber's JSON response body: <c>SUCCESS</c> acknowledges the message, <c>RETRY</c> asks for
/// redelivery, <c>DROP</c> logs a warning and discards it. Any other value is treated as an error
/// and the message is redelivered indefinitely.
/// See https://docs.dapr.io/reference/api/pubsub_api/#expected-http-response.
/// </summary>
public static class DaprPubSubStatus
{
    /// <summary>Message processed; the broker may advance its offset.</summary>
    public const string Success = "SUCCESS";

    /// <summary>Message can never be processed; discard it instead of blocking the partition.</summary>
    public const string Drop = "DROP";

    /// <summary>Message failed transiently; ask the broker to redeliver.</summary>
    public const string Retry = "RETRY";
}

/// <summary>
/// Response body of the pub/sub event delivery endpoint
/// (<c>POST /{domain}/workflows/{workflow}/instances/events</c>).
/// </summary>
/// <remarks>
/// This endpoint is driven by Dapr pub/sub subscriptions, so its body is a protocol contract before
/// it is a payload: the top-level <c>status</c> field is consumed by Dapr itself. Instance DTOs
/// (<c>StartInstanceOutput</c> / <c>TransitionOutput</c>) must never be returned here — their
/// <c>status</c> property serializes to an <c>InstanceStatus</c> code (<c>"A"</c>, <c>"B"</c>, …),
/// which Dapr does not recognize, causing endless redelivery of the same message.
/// </remarks>
public sealed record EventDeliveryResponse
{
    /// <summary>
    /// Dapr protocol signal. Always one of the <see cref="DaprPubSubStatus"/> constants — never an
    /// <c>InstanceStatus</c> code.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>
    /// Why the message was dropped. Diagnostic only — Dapr ignores it; present so a message
    /// discarded as unprocessable is still explainable from the response alone.
    /// </summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>
    /// Instance snapshot, emitted only for <c>sync=true</c> callers (manual testing and integration
    /// tests). Nested on purpose: Dapr only inspects the top-level <c>status</c>, so the instance's
    /// own status code is safe here.
    /// </summary>
    [JsonPropertyName("instance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EventDeliveryInstance? Instance { get; init; }

    /// <summary>Acknowledges a processed (or intentionally ignored) delivery.</summary>
    public static EventDeliveryResponse Succeeded(EventDeliveryInstance? instance = null)
        => new() { Status = DaprPubSubStatus.Success, Instance = instance };

    /// <summary>Discards a delivery that can never succeed, recording why.</summary>
    public static EventDeliveryResponse Dropped(string reason)
        => new() { Status = DaprPubSubStatus.Drop, Reason = reason };
}

/// <summary>
/// Minimal instance projection returned alongside a successful synchronous event delivery.
/// </summary>
/// <param name="Id">Instance identifier.</param>
/// <param name="Key">Business key the event correlated to.</param>
/// <param name="Status">Instance status code (<c>A</c>, <c>B</c>, <c>C</c>, <c>F</c>, <c>P</c>).</param>
public sealed record EventDeliveryInstance(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("status")] string? Status);
