namespace BBT.Workflow.Functions;

/// <summary>
/// HTTP-aware function response payload.
/// </summary>
public sealed class FunctionResponseOutput
{
    public Dictionary<string, dynamic?> Data { get; init; } = [];

    public int? StatusCode { get; init; }

    public Dictionary<string, string>? Headers { get; init; }
}
