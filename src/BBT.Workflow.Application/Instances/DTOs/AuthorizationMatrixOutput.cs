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
/// One <c>availableIn</c> entry in the authorization matrix: a state the transition is offered in,
/// plus any role grants that apply only in that state.
/// </summary>
public sealed class AuthorizationMatrixAvailableInDto
{
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("roles")]
    public List<RoleGrantDto> Roles { get; set; } = [];
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

    /// <summary>
    /// Per-state availability, empty when the transition declares no <c>availableIn</c> (available in
    /// every state). Reported so a client reading the matrix sees the full picture: <see cref="Roles"/>
    /// alone does not reveal a state-scoped narrowing. The matrix is deliberately state-independent, so
    /// these entries are not filtered against any instance's current state.
    /// </summary>
    [JsonPropertyName("availableIn")]
    public List<AuthorizationMatrixAvailableInDto> AvailableIn { get; set; } = [];
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
