using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// How a transition's request body combines ARRAYS with the instance data already stored
/// (vnext-client-sdk-core#58, AB-18). Objects are unaffected — they have always merged key by key.
/// </summary>
/// <remarks>
/// <para>
/// Optional and non-breaking: a transition without <c>arrayMerge</c> keeps the runtime's existing
/// behaviour, <see cref="Replace"/>, where the incoming array wins outright.
/// </para>
/// <para>
/// The two values are deliberately opposite trade-offs, which is why this is a choice rather than a
/// fix. <see cref="Replace"/> lets a caller REMOVE an item by omitting it, but a caller holding a
/// stale copy silently erases items another request just added. <see cref="Merge"/> makes concurrent
/// additions safe, but removal-by-omission no longer works — an item disappears only if something
/// else rewrites the array under <see cref="Replace"/>.
/// </para>
/// <para>
/// String enum: only <c>R</c> and <c>M</c> are accepted (case-insensitive on read, though the schema
/// constrains authoring to the upper-case forms). An unknown value is rejected at deserialization,
/// which is what makes a mistyped value fail the component publish rather than run.
/// </para>
/// </remarks>
[JsonConverter(typeof(IEquatableJsonConverter<ArrayMergeStrategy>))]
public sealed class ArrayMergeStrategy : IEquatable<ArrayMergeStrategy>
{
    /// <summary>
    /// The incoming array replaces the stored one outright. The runtime default, and the behaviour
    /// every transition has today: omitting an item is how a caller deletes it.
    /// </summary>
    public static readonly ArrayMergeStrategy Replace = new("R", "Replace");

    /// <summary>
    /// The stored array is kept and the incoming items are folded in: an item that matches one
    /// already present replaces it, anything new is appended, and duplicates collapse. Items match by
    /// their <c>id</c> when both sides are objects carrying one, otherwise by exact value.
    /// </summary>
    public static readonly ArrayMergeStrategy Merge = new("M", "Merge");

    public string Code { get; }
    public string Description { get; }

    private ArrayMergeStrategy()
    {
    }

    private ArrayMergeStrategy(string code, string description)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    /// <summary>Resolves the strategy from its authored code (case-insensitive). Throws on any other value.</summary>
    public static ArrayMergeStrategy FromCode(string code)
    {
        return code?.Trim().ToUpperInvariant() switch
        {
            "R" => Replace,
            "M" => Merge,
            _ => throw new ArgumentException($"Unknown array merge strategy: {code}")
        };
    }

    /// <summary>True when this is <see cref="Merge"/>.</summary>
    public bool IsMerge => Code == Merge.Code;

    public bool Equals(ArrayMergeStrategy? other) => Code == other?.Code;

    public override bool Equals(object? obj) => obj is ArrayMergeStrategy other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Code);

    public override string ToString() => $"{Description} ({Code})";
}
