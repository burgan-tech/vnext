using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Declares whether a flow keeps a per-instance history: <c>full</c> (default — every data append,
/// transition and task journal row is persisted) or <c>none</c> (a one-shot flow: no
/// <c>InstanceTransitions</c> / <c>InstanceTasks</c> rows, and <c>InstancesData</c> is buffered in memory
/// and written only at Finish, at a SubFlow handoff or on an in-pipeline fault). vnext#1006.
/// </summary>
/// <remarks>
/// <para>
/// Authored at the root of the workflow <c>attributes</c> (sibling of <c>type</c> and
/// <c>executionType</c>), never under <c>config</c>. Absent ⇒ <see cref="Full"/>.
/// </para>
/// <para>
/// String enum: only <c>none</c> and <c>full</c> are accepted (case-insensitive on read, though the schema
/// constrains authoring to the lower-case forms). An unknown value is rejected at deserialization.
/// </para>
/// </remarks>
[JsonConverter(typeof(IEquatableJsonConverter<HistoryMode>))]
public sealed class HistoryMode : IEquatable<HistoryMode>
{
    /// <summary>One-shot flow: no transition/task history, a single buffered data write.</summary>
    public static readonly HistoryMode None = new("none", "No history");

    /// <summary>Default: every data version, transition and task journal row is persisted.</summary>
    public static readonly HistoryMode Full = new("full", "Full history");

    public string Code { get; }
    public string Description { get; }

    private HistoryMode()
    {
    }

    private HistoryMode(string code, string description)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    /// <summary>Resolves the value object from its authored code (case-insensitive). Throws on any other value.</summary>
    public static HistoryMode FromCode(string code)
    {
        return code?.Trim().ToLowerInvariant() switch
        {
            "none" => None,
            "full" => Full,
            _ => throw new ArgumentException($"Unknown history mode: {code}")
        };
    }

    /// <summary>True when this is <see cref="None"/>.</summary>
    public bool IsNone => Code == None.Code;

    public bool Equals(HistoryMode? other) => Code == other?.Code;

    public override bool Equals(object? obj) => obj is HistoryMode other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Code);

    public override string ToString() => $"{Description} ({Code})";
}
