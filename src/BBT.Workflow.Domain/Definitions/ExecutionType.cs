using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Declares how a flow or a transition executes: <c>SYNC</c> (the request blocks until the pipeline
/// reaches a rest point and returns the full instance) or <c>ASYNC</c> (the request is accepted and the
/// pipeline runs in the background via the scheduler). vnext#1003.
/// </summary>
/// <remarks>
/// <para>
/// Optional and non-breaking: a flow or transition without an <c>executionType</c> keeps the runtime's
/// existing behaviour, where the caller's <c>sync</c> query parameter (default <c>false</c> ⇒ async)
/// chooses the mode. When present, the definition is the source of truth and the query parameter is
/// ignored. A transition's setting (the inner definition) wins over the flow's (the outer definition).
/// </para>
/// <para>
/// String enum: only <c>SYNC</c> and <c>ASYNC</c> are accepted (case-insensitive on read, though the
/// schema constrains authoring to the upper-case forms). An unknown value is rejected at deserialization.
/// </para>
/// </remarks>
[JsonConverter(typeof(IEquatableJsonConverter<ExecutionType>))]
public sealed class ExecutionType : IEquatable<ExecutionType>
{
    /// <summary>The request blocks until the pipeline settles and returns the full instance.</summary>
    public static readonly ExecutionType Sync = new("SYNC", "Synchronous");

    /// <summary>The request is accepted and the pipeline runs in the background (scheduler).</summary>
    public static readonly ExecutionType Async = new("ASYNC", "Asynchronous");

    public string Code { get; }
    public string Description { get; }

    private ExecutionType()
    {
    }

    private ExecutionType(string code, string description)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    /// <summary>Resolves the value object from its authored code (case-insensitive). Throws on any other value.</summary>
    public static ExecutionType FromCode(string code)
    {
        return code?.Trim().ToUpperInvariant() switch
        {
            "SYNC" => Sync,
            "ASYNC" => Async,
            _ => throw new ArgumentException($"Unknown execution type: {code}")
        };
    }

    /// <summary>True when this is <see cref="Async"/>.</summary>
    public bool IsAsync => Code == Async.Code;

    public bool Equals(ExecutionType? other) => Code == other?.Code;

    public override bool Equals(object? obj) => obj is ExecutionType other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Code);

    public override string ToString() => $"{Description} ({Code})";
}
