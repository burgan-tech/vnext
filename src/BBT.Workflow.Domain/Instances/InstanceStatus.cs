using System.Text.Json.Serialization;

namespace BBT.Workflow.Instances;

[JsonConverter(typeof(IEquatableJsonConverter<InstanceStatus>))]
public sealed class InstanceStatus : IEquatable<InstanceStatus>
{
    public static readonly InstanceStatus Busy = new("B", "Busy");
    public static readonly InstanceStatus Active = new("A", "Active");
    public static readonly InstanceStatus Passive = new("P", "Passive");
    public static readonly InstanceStatus Completed = new("C", "Completed");
    public static readonly InstanceStatus Faulted = new("F", "Faulted");

    public string Code { get; }
    public string Description { get; }

    private InstanceStatus()
    {
    }

    private InstanceStatus(string code, string description)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    public static InstanceStatus FromCode(string code)
    {
        return code switch
        {
            "B" => Busy,
            "A" => Active,
            "P" => Passive,
            "C" => Completed,
            "F" => Faulted,
            _ => throw new ArgumentException($"Unknown status code: {code}")
        };
    }

    /// <summary>
    /// Lenient counterpart of <see cref="FromCode"/>: null for a null, empty or unrecognised code
    /// instead of throwing. Used where an unknown value is a legitimate answer — a cross-domain hop
    /// whose far side does not report a status yet must be read as "unknown", never guessed.
    /// </summary>
    public static InstanceStatus? TryFromCode(string? code)
    {
        return code switch
        {
            "B" => Busy,
            "A" => Active,
            "P" => Passive,
            "C" => Completed,
            "F" => Faulted,
            _ => null
        };
    }

    public override bool Equals(object? obj)
    {
        return obj is InstanceStatus other && Equals(other);
    }

    public bool Equals(InstanceStatus? other)
    {
        return Code == other?.Code;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Code);
    }

    public override string ToString()
    {
        return $"{Description} ({Code})";
    }
}