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
}
