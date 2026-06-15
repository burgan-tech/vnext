namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// Execution-ready binding for SOAP tasks.
/// Carries all configuration needed by SoapTaskInvoker without any domain dependencies.
/// </summary>
public sealed record SoapTaskBinding
{
    public required string Url { get; init; }
    public string SoapAction { get; init; } = string.Empty;
    public string SoapVersion { get; init; } = "1.1";

    /// <summary>Raw XML SOAP envelope body string.</summary>
    public string? Body { get; init; }

    /// <summary>Additional HTTP headers serialized as a JSON object string.</summary>
    public string? Headers { get; init; }

    public int TimeoutSeconds { get; init; } = 30;
    public bool ValidateSSL { get; init; } = true;

    /// <summary>
    /// HTTP status codes treated as successful even when outside the 2xx range.
    /// Supports exact codes ("500") and wildcard patterns ("5xx", "50x").
    /// SOAP faults typically come back over HTTP 500, so ["500"] is a common value.
    /// </summary>
    public IReadOnlyList<string>? AcceptedStatusCodes { get; init; }
}
