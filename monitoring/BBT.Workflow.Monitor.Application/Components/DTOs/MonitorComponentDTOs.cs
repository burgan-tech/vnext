using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace BBT.Workflow.Monitor.Components.DTOs;

/// <summary>
/// Input for querying workflow components.
/// The client specifies the component type in the request; a single endpoint serves all component types.
/// </summary>
public sealed class MonitorGetComponentsInput
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Component type to query.
    /// Supported values: sys-flows, sys-tasks, sys-schemas, sys-extensions, sys-functions, sys-views.
    /// </summary>
    [Required]
    public string ComponentType { get; set; } = string.Empty;

    /// <summary>
    /// Optional component key. When provided, returns that component only (404 if missing).
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Optional version filter. When provided, resolves the component at that version.
    /// When omitted, the latest version is returned.
    /// </summary>
    public string? Version { get; set; }
}

/// <summary>
/// Generic component query response.
/// Items are serialised as raw JSON elements so that all component types share a single contract.
/// The client interprets <see cref="ComponentType"/> to deserialise each item accordingly.
/// </summary>
public sealed class MonitorComponentResponse
{
    /// <summary>
    /// The component type that was queried (mirrors <see cref="MonitorGetComponentsInput.ComponentType"/>).
    /// </summary>
    public string ComponentType { get; set; } = string.Empty;

    /// <summary>
    /// The result items. Each element is the JSON representation of the matching component.
    /// </summary>
    public List<JsonElement> Items { get; set; } = [];
}

/// <summary>
/// Supported component type constants for monitor queries.
/// </summary>
public static class MonitorComponentTypes
{
    /// <summary>Workflow (flow) definitions.</summary>
    public const string Flows = "sys-flows";

    /// <summary>Task definitions.</summary>
    public const string Tasks = "sys-tasks";

    /// <summary>Schema definitions.</summary>
    public const string Schemas = "sys-schemas";

    /// <summary>Extension definitions.</summary>
    public const string Extensions = "sys-extensions";

    /// <summary>Function definitions.</summary>
    public const string Functions = "sys-functions";

    /// <summary>View definitions.</summary>
    public const string Views = "sys-views";

    /// <summary>Mapping (script-library) definitions.</summary>
    public const string Mappings = "sys-mappings";
}

/// <summary>Input for querying component type counts for a domain.</summary>
public sealed class MonitorGetComponentStatsInput
{
    /// <summary>The tenant/domain key.</summary>
    [Required]
    public string Domain { get; set; } = string.Empty;
}

/// <summary>Per-type component counts for a domain.</summary>
public sealed class MonitorComponentStatsResponse
{
    /// <summary>Number of published workflow (flow) definitions.</summary>
    public int Flows { get; set; }

    /// <summary>Number of published task definitions.</summary>
    public int Tasks { get; set; }

    /// <summary>Number of published schema definitions.</summary>
    public int Schemas { get; set; }

    /// <summary>Number of published view definitions.</summary>
    public int Views { get; set; }

    /// <summary>Number of published function definitions.</summary>
    public int Functions { get; set; }

    /// <summary>Number of published extension definitions.</summary>
    public int Extensions { get; set; }

    /// <summary>Number of published mapping (script-library) definitions.</summary>
    public int Mappings { get; set; }

    /// <summary>Sum of all component counts across all types.</summary>
    public int Total => Flows + Tasks + Schemas + Views + Functions + Extensions + Mappings;
}

/// <summary>Summary list response for a single component type — lightweight alternative to /definition.</summary>
public sealed class MonitorComponentSummaryResponse
{
    /// <summary>The component type that was queried.</summary>
    public string ComponentType { get; set; } = string.Empty;

    /// <summary>Summary items — one per published component.</summary>
    public List<MonitorComponentSummaryItem> Items { get; set; } = [];
}

/// <summary>Lightweight summary of a single published component (list query, no key).</summary>
public sealed class MonitorComponentSummaryItem
{
    /// <summary>Component key.</summary>
    public string? Key { get; set; }

    /// <summary>Semantic version.</summary>
    public string? Version { get; set; }

    /// <summary>Owning domain.</summary>
    public string? Domain { get; set; }

    /// <summary>Human-readable labels (multi-language). Null when the component type does not define labels.</summary>
    public List<MonitorComponentLabel>? Labels { get; set; }

    /// <summary>
    /// Component type discriminator when present in the definition (e.g. <c>WorkflowType</c>, <c>TaskType</c>, <c>ViewType</c>).
    /// Null for component types that do not carry a <c>type</c> field.
    /// </summary>
    public JsonElement? Type { get; set; }

    /// <summary>Free-text developer notes from the definition's <c>_comment</c> field. Null when absent.</summary>
    public string? Comment { get; set; }
}

/// <summary>
/// Single-component detail response returned when a <c>key</c> is provided.
/// Flat structure (no <c>items</c> wrapper), includes the component's <c>flow</c> identifier and all published versions.
/// </summary>
public sealed class MonitorComponentDetailResponse
{
    /// <summary>Component key.</summary>
    public string? Key { get; set; }

    /// <summary>Resolved semantic version.</summary>
    public string? Version { get; set; }

    /// <summary>Owning domain.</summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Component stream identifier (e.g. <c>sys-flows</c>, <c>sys-tasks</c>).
    /// Reflects the <c>flow</c> field stored inside the component definition.
    /// </summary>
    public string? Flow { get; set; }

    /// <summary>Human-readable labels (multi-language). Null when the component type does not define labels.</summary>
    public List<MonitorComponentLabel>? Labels { get; set; }

    /// <summary>
    /// Component type discriminator when present in the definition (e.g. <c>WorkflowType</c>, <c>TaskType</c>, <c>ViewType</c>).
    /// Null for component types that do not carry a <c>type</c> field.
    /// </summary>
    public JsonElement? Type { get; set; }

    /// <summary>Free-text developer notes from the definition's <c>_comment</c> field. Null when absent.</summary>
    public string? Comment { get; set; }

    /// <summary>All published versions of this component, sorted descending (newest first).</summary>
    public List<string>? Versions { get; set; }
}

/// <summary>A localised display label for a component.</summary>
public sealed class MonitorComponentLabel
{
    /// <summary>ISO 639 language code (e.g. "tr", "en").</summary>
    public string? Language { get; set; }

    /// <summary>Display text in the given language.</summary>
    public string? Label { get; set; }
}

/// <summary>P15 — all component dependencies of a workflow definition.</summary>
public sealed class MonitorDependencyResponse
{
    /// <summary>The queried workflow identity.</summary>
    public MonitorComponentRef Workflow { get; set; } = new();
    /// <summary>All component references used by this workflow.</summary>
    public MonitorDependencies Dependencies { get; set; } = new();
}

/// <summary>Component references grouped by type.</summary>
public sealed class MonitorDependencies
{
    /// <summary>Task dependencies.</summary>
    public List<MonitorDependencyRef> Tasks { get; set; } = [];
    /// <summary>Schema dependencies.</summary>
    public List<MonitorDependencyRef> Schemas { get; set; } = [];
    /// <summary>View dependencies.</summary>
    public List<MonitorDependencyRef> Views { get; set; } = [];
    /// <summary>Function dependencies.</summary>
    public List<MonitorDependencyRef> Functions { get; set; } = [];
    /// <summary>Extension dependencies.</summary>
    public List<MonitorDependencyRef> Extensions { get; set; } = [];
    /// <summary>SubFlow dependencies.</summary>
    public List<MonitorDependencyRef> SubFlows { get; set; } = [];
}

/// <summary>Minimal component identity.</summary>
public sealed class MonitorComponentRef
{
    /// <summary>Component key.</summary>
    public string? Key { get; set; }
    /// <summary>Component version.</summary>
    public string? Version { get; set; }
    /// <summary>Owning domain.</summary>
    public string? Domain { get; set; }
}

/// <summary>A component dependency with its reference site in the workflow definition.</summary>
public sealed class MonitorDependencyRef
{
    /// <summary>Component key.</summary>
    public string? Key { get; set; }
    /// <summary>Component version.</summary>
    public string? Version { get; set; }
    /// <summary>Owning domain.</summary>
    public string? Domain { get; set; }
    /// <summary>Where in the workflow definition this dependency is used (e.g. "state:approve/onEntries", "transition:submit").</summary>
    public string? ReferencedFrom { get; set; }
}
