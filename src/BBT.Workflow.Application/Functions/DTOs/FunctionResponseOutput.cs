namespace BBT.Workflow.Functions;

/// <summary>
/// HTTP-aware function response payload.
/// </summary>
public sealed class FunctionResponseOutput
{
    public object? Data { get; init; }

    public int? StatusCode { get; init; }

    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// The raw response body is binary (<see cref="Data"/> is a <c>byte[]</c>, or its base64 after a function
    /// response cache round trip): it is written as bytes, not as a JSON string (vnext-client-sdk-core#99/#142-E).
    /// Null/false for every other response.
    /// </summary>
    public bool? IsBinary { get; init; }
}
