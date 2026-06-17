using BBT.Workflow.Runtime;

namespace BBT.Workflow.Components;

/// <summary>
/// Enumerates the seven discoverable vNext runtime component types exposed by the
/// Component Discovery API. The names map 1:1 to the URL tokens used on the
/// <c>/{domain}/components/{type}</c> routes.
/// </summary>
public enum ComponentType
{
    /// <summary>Workflow definitions (<c>sys-flows</c>).</summary>
    Workflows,

    /// <summary>Task definitions (<c>sys-tasks</c>).</summary>
    Tasks,

    /// <summary>Function definitions (<c>sys-functions</c>).</summary>
    Functions,

    /// <summary>View definitions (<c>sys-views</c>).</summary>
    Views,

    /// <summary>Extension definitions (<c>sys-extensions</c>).</summary>
    Extensions,

    /// <summary>Schema definitions (<c>sys-schemas</c>).</summary>
    Schemas,

    /// <summary>Mapping (reusable script-library) definitions (<c>sys-mappings</c>).</summary>
    Mappings
}

/// <summary>
/// Helper conversions between the URL token, <see cref="ComponentType"/>, and the
/// runtime <c>sys-*</c> component-type key.
/// </summary>
public static class ComponentTypeExtensions
{
    /// <summary>
    /// Attempts to parse a URL token (e.g. <c>workflows</c>) into a <see cref="ComponentType"/>.
    /// Case-insensitive.
    /// </summary>
    public static bool TryParse(string? token, out ComponentType type)
    {
        type = default;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        return Enum.TryParse(token, ignoreCase: true, out type) && Enum.IsDefined(type);
    }

    /// <summary>
    /// Returns the lowercase URL token for a <see cref="ComponentType"/> (e.g. <c>workflows</c>).
    /// </summary>
    public static string ToToken(this ComponentType type) => type.ToString().ToLowerInvariant();

    /// <summary>
    /// Returns the runtime <c>sys-*</c> component-type key for a <see cref="ComponentType"/>.
    /// </summary>
    public static string ToComponentTypeKey(this ComponentType type) => type switch
    {
        ComponentType.Workflows => RuntimeSysSchemaInfo.Flows,
        ComponentType.Tasks => RuntimeSysSchemaInfo.Tasks,
        ComponentType.Functions => RuntimeSysSchemaInfo.Functions,
        ComponentType.Views => RuntimeSysSchemaInfo.Views,
        ComponentType.Extensions => RuntimeSysSchemaInfo.Extensions,
        ComponentType.Schemas => RuntimeSysSchemaInfo.Schemas,
        ComponentType.Mappings => RuntimeSysSchemaInfo.Mappings,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}
