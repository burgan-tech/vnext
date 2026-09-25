using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Per-function opt-in for the function-execution journal (vnext-client-sdk-core#60). A function whose
/// <c>executionLog</c> is <c>ENABLED</c> has every invocation recorded in the <c>FunctionExecutions</c>
/// table (and served by the metrics endpoints); <c>DISABLED</c> — the default when the field is absent —
/// records none. So existing functions and any authored without the field keep the no-logging behaviour;
/// only functions that opt in are logged.
/// </summary>
/// <remarks>
/// This gates only the durable execution journal. It is independent of OTel/APM tracing, which is
/// always emitted (the <c>Function.Execute</c> span) regardless of this setting.
/// </remarks>
[JsonConverter(typeof(IEquatableJsonConverter<ExecutionLogSetting>))]
public sealed class ExecutionLogSetting : IEquatable<ExecutionLogSetting>
{
    /// <summary>Executions are not journaled. The default when the field is absent.</summary>
    public static readonly ExecutionLogSetting Disabled = new("DISABLED", "Disabled");

    /// <summary>Every invocation is recorded in the function-execution journal.</summary>
    public static readonly ExecutionLogSetting Enabled = new("ENABLED", "Enabled");

    public string Code { get; }
    public string Description { get; }

    private ExecutionLogSetting()
    {
    }

    private ExecutionLogSetting(string code, string description)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    /// <summary>
    /// Resolves the setting from its authored code. Case-insensitive so <c>enabled</c>/<c>Enabled</c>
    /// authored variants resolve, though the schema constrains authoring to the upper-case forms.
    /// </summary>
    public static ExecutionLogSetting FromCode(string code)
    {
        return code?.Trim().ToUpperInvariant() switch
        {
            "ENABLED" => Enabled,
            "DISABLED" => Disabled,
            _ => throw new ArgumentException($"Unknown execution log setting: {code}")
        };
    }

    public bool Equals(ExecutionLogSetting? other) => Code == other?.Code;

    public override bool Equals(object? obj) => obj is ExecutionLogSetting other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Code);

    public override string ToString() => $"{Description} ({Code})";
}
