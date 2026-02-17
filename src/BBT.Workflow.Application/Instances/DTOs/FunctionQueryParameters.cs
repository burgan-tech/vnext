using System.Text.Json.Serialization;

namespace BBT.Workflow.Instances.DTOs;

public class FunctionQueryParameters
{
    [JsonPropertyName("platform")]
    public string? Platform { get; set; } = string.Empty;
    
    /// <summary>Optional component version (workflow or function). Empty/null = latest. Used e.g. for authorize and authorization matrix.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; } = null;
    
    [JsonPropertyName("extensions")]
    public string[]? Extensions { get; set; } = null;
    
    [JsonPropertyName("transitionKey")]
    public string? TransitionKey { get; set; } = null;

    /// <summary>Role for authorize function (e.g. morph-idm.maker).</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; } = null;

    /// <summary>Function key for authorize function (function-level check). Mutually exclusive with transitionKey.</summary>
    [JsonPropertyName("functionKey")]
    public string? FunctionKey { get; set; } = null;

    /// <summary>When true, authorize evaluates state-based query roles (instance only). Mutually exclusive with transitionKey and functionKey.</summary>
    [JsonPropertyName("queryRoles")]
    public bool? QueryRoles { get; set; } = null;
}
