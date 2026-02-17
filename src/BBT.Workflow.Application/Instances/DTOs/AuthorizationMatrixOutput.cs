using System.Text.Json.Serialization;

namespace BBT.Workflow.Instances;

/// <summary>
/// Single state entry in the authorization matrix.
/// </summary>
public sealed class AuthorizationMatrixStateDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("queryRoles")]
    public List<RoleGrantDto> QueryRoles { get; set; } = [];
}

/// <summary>
/// Single transition entry in the authorization matrix.
/// </summary>
public sealed class AuthorizationMatrixTransitionDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("roles")]
    public List<RoleGrantDto> Roles { get; set; } = [];
}

/// <summary>
/// Single function entry in the authorization matrix.
/// </summary>
public sealed class AuthorizationMatrixFunctionDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("roles")]
    public List<RoleGrantDto> Roles { get; set; } = [];
}

/// <summary>
/// Role grant for DTOs (role + grant allow/deny).
/// </summary>
public sealed class RoleGrantDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("grant")]
    public string Grant { get; set; } = "allow";
}

/// <summary>
/// Authorization matrix for a workflow: root queryRoles, states, transitions, and functions (workflow-referenced) with their roles.
/// </summary>
public sealed class AuthorizationMatrixOutput
{
    [JsonPropertyName("workflow")]
    public string Workflow { get; set; } = string.Empty;

    [JsonPropertyName("queryRoles")]
    public List<RoleGrantDto> QueryRoles { get; set; } = [];

    [JsonPropertyName("states")]
    public List<AuthorizationMatrixStateDto> States { get; set; } = [];

    [JsonPropertyName("transitions")]
    public List<AuthorizationMatrixTransitionDto> Transitions { get; set; } = [];

    [JsonPropertyName("functions")]
    public List<AuthorizationMatrixFunctionDto> Functions { get; set; } = [];
}
