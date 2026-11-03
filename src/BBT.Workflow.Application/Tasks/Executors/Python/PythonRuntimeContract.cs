using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Version 1 of the wire contract between the Orchestration host and a domain's Python runtime
/// (<c>POST v1/invoke</c>). The same shapes are documented for runtime authors in
/// <c>docs/runtime/python-task.md</c> and implemented by <c>runtimes/python/vnext_runtime</c>.
/// </summary>
internal static class PythonRuntimeContract
{
    public const string Version = "1";

    internal sealed class InvokeRequest
    {
        [JsonPropertyName("contractVersion")]
        public string ContractVersion { get; init; } = Version;

        [JsonPropertyName("handler")]
        public required string Handler { get; init; }

        [JsonPropertyName("input")]
        public JsonElement? Input { get; init; }

        [JsonPropertyName("timeoutMs")]
        public int TimeoutMs { get; init; }

        [JsonPropertyName("context")]
        public required InvokeContext Context { get; init; }
    }

    internal sealed class InvokeContext
    {
        [JsonPropertyName("domain")]
        public string Domain { get; init; } = string.Empty;

        [JsonPropertyName("workflow")]
        public string Workflow { get; init; } = string.Empty;

        [JsonPropertyName("workflowVersion")]
        public string? WorkflowVersion { get; init; }

        [JsonPropertyName("instanceId")]
        public Guid InstanceId { get; init; }

        [JsonPropertyName("taskKey")]
        public string TaskKey { get; init; } = string.Empty;

        [JsonPropertyName("correlationId")]
        public string? CorrelationId { get; init; }
    }

    /// <summary>
    /// Reads <c>output</c> from a success body and <c>error.code</c> / <c>error.message</c> from a
    /// failure body. A body that is not the contract shape yields nulls, never an exception.
    /// </summary>
    public static (JsonElement? Output, string? ErrorCode, string? ErrorMessage) ReadResponse(object? data)
    {
        if (data is not JsonElement { ValueKind: JsonValueKind.Object } body)
        {
            return (null, null, null);
        }

        JsonElement? output = body.TryGetProperty("output", out var o) ? o.Clone() : null;
        string? code = null, message = null;
        if (body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }

        return (output, code, message);
    }
}
