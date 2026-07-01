using BBT.Workflow.Monitor.Components.DTOs;

namespace BBT.Workflow.Monitor.Components.Filters;

/// <summary>
/// Static registry that maps each component type to its allowed filter field names.
/// Used at request time to validate that no unsupported filter is applied.
/// </summary>
public static class ComponentFilterDescriptor
{
    private static readonly IReadOnlySet<string> CommonFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "createdAt", "modifiedAt", "tags", "flowVersion", "key", "version"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> TypeSpecificFields =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [MonitorComponentTypes.Flows]      = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "definitionType" },
            [MonitorComponentTypes.Tasks]      = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "definitionType" },
            [MonitorComponentTypes.Schemas]    = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "definitionType" },
            [MonitorComponentTypes.Views]      = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "definitionType", "display", "renderer" },
            [MonitorComponentTypes.Functions]  = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scope" },
            [MonitorComponentTypes.Extensions] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "definitionType", "scope" },
            [MonitorComponentTypes.Mappings]   = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name" },
        };

    /// <summary>Returns all allowed filter field names for the given component type.</summary>
    public static IReadOnlySet<string> AllowedFor(string componentType)
    {
        var specific = TypeSpecificFields.GetValueOrDefault(componentType, new HashSet<string>());
        return CommonFields.Union(specific, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the field names that are set in the filter but not allowed for the given component type.
    /// An empty list means the filter is valid for this type.
    /// </summary>
    public static IReadOnlyList<string> FindDisallowed(string componentType, MonitorComponentFilterInput filter)
    {
        var allowed = AllowedFor(componentType);
        return filter.SetFields()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !allowed.Contains(f))
            .ToList();
    }
}
