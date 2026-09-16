using System.Text.Json.Serialization;
using BBT.Aether;

namespace BBT.Workflow.Instances;

/// <summary>
/// How an instance was STARTED: <see cref="Root"/> directly, <see cref="SubFlow"/> as a parent's
/// SubFlow child, or <see cref="SubProcess"/> as a parent's SubProcess child. Stamped once at
/// creation and never updated.
/// </summary>
/// <remarks>
/// Deliberately independent of <see cref="Instance.IsSubFlow"/> / <see cref="Instance.IsSubItem"/>,
/// which read <c>parent.flowtype</c> and answer "what kind of thing is this definition". Those stay
/// exactly as they are; this type answers "how did this row come into existence", which is the only
/// question a report can ask of a finished instance.
/// </remarks>
[JsonConverter(typeof(IEquatableJsonConverter<InstanceType>))]
public sealed class InstanceType : IEquatable<InstanceType>
{
    public static readonly InstanceType Root = new("R", "Root");
    public static readonly InstanceType SubFlow = new("S", "Sub Flow");
    public static readonly InstanceType SubProcess = new("P", "Sub Process");

    public string Code { get; }
    public string Description { get; }

    private InstanceType()
    {
    }

    private InstanceType(string code, string description)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    /// <summary>
    /// The single derivation rule, applied once at creation from the metadata the starter supplied.
    /// A non-empty <c>parent.id</c> means this instance was started as a child, and
    /// <c>parent.flowtype</c> says which kind; anything else is a <see cref="Root"/>.
    /// </summary>
    /// <remarks>
    /// <c>parent.flowtype</c> ALONE is not a discriminator, which is why <c>parent.id</c> is the
    /// gate: <see cref="Instance.SetInfoMetadata"/> <c>TryAdd</c>s the instance's OWN workflow type
    /// code into that key when it is absent, so a workflow whose definition declares
    /// <c>type: "S"</c> and is started directly through the API carries
    /// <c>parent.flowtype: "S"</c> with no parent at all.
    /// </remarks>
    public static InstanceType FromStartMetadata(ExtraPropertyDictionary? metadata)
    {
        if (metadata is null)
        {
            return Root;
        }

        if (!metadata.TryGetValue(DomainConsts.MetaDataKeys.Id, out var rawParentId)
            || rawParentId is null
            || !Guid.TryParse(rawParentId.ToString(), out var parentId)
            || parentId == Guid.Empty)
        {
            return Root;
        }

        if (!metadata.TryGetValue(DomainConsts.MetaDataKeys.FlowType, out var rawFlowType))
        {
            return Root;
        }

        return rawFlowType?.ToString() switch
        {
            "S" => SubFlow,
            "P" => SubProcess,
            _ => Root
        };
    }

    public static InstanceType FromCode(string code)
    {
        return code switch
        {
            "R" => Root,
            "S" => SubFlow,
            "P" => SubProcess,
            _ => throw new ArgumentException($"Unknown instance type code: {code}")
        };
    }

    /// <summary>
    /// Lenient counterpart of <see cref="FromCode"/>: null for a null, empty or unrecognised code
    /// instead of throwing. Used where an unknown value is a legitimate answer — a cross-domain hop
    /// whose far side runs a runtime that predates this field reports no type at all.
    /// </summary>
    public static InstanceType? TryFromCode(string? code)
    {
        return code switch
        {
            "R" => Root,
            "S" => SubFlow,
            "P" => SubProcess,
            _ => null
        };
    }

    public override bool Equals(object? obj)
    {
        return obj is InstanceType other && Equals(other);
    }

    public bool Equals(InstanceType? other)
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
