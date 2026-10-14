namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// The roles a schema component declares in <c>attributes.type</c>. The role selects the vocabulary the schema is
/// authored against (vnext-schema role vocabularies): <see cref="Master"/> is a flow's stored record; <see cref="Transition"/>
/// and <see cref="Function"/> are request bodies; <see cref="Component"/> is an embedded view's own input contract.
/// The type is optional — absent or empty means no role (a schema serving more than one role).
/// </summary>
public static class SchemaRoleTypes
{
    /// <summary>A flow's master (data) schema — the only role that permits <c>x-indexed</c>.</summary>
    public const string Master = "master";

    /// <summary>A transition body schema.</summary>
    public const string Transition = "transition";

    /// <summary>The dataSchema of an embedded view component.</summary>
    public const string Component = "component";

    /// <summary>A function <c>inputSchema</c>.</summary>
    public const string Function = "function";

    /// <summary>Every declared role, exact spelling.</summary>
    public static readonly IReadOnlyList<string> All = [Master, Transition, Component, Function];

    /// <summary>True for a missing or empty type, or one of the roles (exact, case-sensitive).</summary>
    public static bool IsValid(string? type) => string.IsNullOrEmpty(type) || All.Contains(type, StringComparer.Ordinal);

    /// <summary>True for a role whose schema is a request body or a component contract — never a stored record.</summary>
    public static bool IsNonRecordRole(string? type) => type is Transition or Component or Function;
}
