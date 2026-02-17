using System.Text.Json.Serialization;

namespace BBT.Workflow.Instances.DTOs;

public class FunctionListQueryParameters
{

    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 10;
    
    [JsonPropertyName("sort")]
    public string? Sort { get; set; } = string.Empty;

    /// <summary>
    /// Alias for sort; same JSON format as sort. Only applied when function type is Data.
    /// If both sort and orderBy provided, orderBy wins.
    /// </summary>
    [JsonPropertyName("orderBy")]
    public string? OrderBy { get; set; }
}
