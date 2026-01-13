using System.Text.Json.Serialization;

namespace BBT.Workflow.Instances;

/// <summary>
/// Summary of a group with aggregation results
/// Used in groupBy queries to represent grouped data with aggregations
/// </summary>
public sealed class GroupSummary
{
    /// <summary>
    /// Group name/value (from the groupBy field)
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Count of records in this group
    /// </summary>
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Count { get; set; }

    /// <summary>
    /// Sum of numeric field values in this group
    /// </summary>
    [JsonPropertyName("sum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Sum { get; set; }

    /// <summary>
    /// Average of numeric field values in this group
    /// </summary>
    [JsonPropertyName("avg")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Avg { get; set; }

    /// <summary>
    /// Minimum value in this group
    /// </summary>
    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Min { get; set; }

    /// <summary>
    /// Maximum value in this group
    /// </summary>
    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Max { get; set; }
}

