using System.Text.Json.Serialization;

namespace BBT.Workflow.Instances.DTOs;

public class FunctionQueryParameters
{
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

    /// <summary>When true, authorize evaluates state-based query roles (instance only). Mutually exclusive with transitionKey, functionKey and ack.</summary>
    [JsonPropertyName("queryRoles")]
    public bool? QueryRoles { get; set; } = null;

    /// <summary>
    /// When true, authorize evaluates the long-poll acknowledge gate for the instance
    /// (<c>state.interaction.longPoll</c>, both the roles and the rule arm). Mutually exclusive with
    /// transitionKey, functionKey and queryRoles. This is the pre-flight for
    /// <c>POST .../instances/{instance}/longpoll/ack</c>, which is the one state-changing surface the
    /// middle tier has no other way to ask about.
    /// </summary>
    [JsonPropertyName("ack")]
    public bool? Ack { get; set; } = null;
}
