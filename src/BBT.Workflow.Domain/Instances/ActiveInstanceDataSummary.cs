using System.Text.Json;

namespace BBT.Workflow.Instances;

/// <summary>
/// Lightweight projection of an active Instance joined with its InstanceData,
/// containing only the fields needed for component list/summary queries.
/// </summary>
public sealed record ActiveInstanceDataSummary(
    string? Key,
    string FlowVersion,
    List<string> Tags,
    DateTime CreatedAt,
    DateTime? ModifiedAt,
    JsonElement DataBlob,
    string DataVersion,
    bool IsLatest);
