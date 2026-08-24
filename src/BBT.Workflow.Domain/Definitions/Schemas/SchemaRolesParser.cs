using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Parses the "x-roles" vocabulary from a JSON Schema (master schema).
/// Extracts per-property role grants: path → RoleGrant[] for field-level visibility.
/// Traversal is delegated to <see cref="SchemaAnnotationWalker"/> so this parser agrees with
/// every other vocabulary parser about what a property path is.
/// </summary>
public static class SchemaRolesParser
{
    private const string RolesKey = "x-roles";

    /// <summary>
    /// Parses the schema and returns a map of property path to role grants.
    /// Path format: dot-separated, with "[]" for array item schemas
    /// (e.g. "amount", "nested.field", "cards[].number").
    /// Properties without "x-roles" are not included (treated as visible to all).
    /// </summary>
    /// <param name="schemaRoot">The root JsonElement of the schema (object with optional "properties").</param>
    /// <returns>Map of property path to list of role grants; empty if schema has no roles.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<RoleGrant>> ParsePropertyRoles(JsonElement schemaRoot)
    {
        var result = new Dictionary<string, IReadOnlyList<RoleGrant>>(StringComparer.Ordinal);

        foreach (var node in SchemaAnnotationWalker.Walk(schemaRoot))
        {
            if (!node.Schema.TryGetProperty(RolesKey, out var rolesElement) ||
                rolesElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var grants = ParseRoleGrants(rolesElement);
            if (grants.Count > 0)
                result[node.Path] = grants;
        }

        return result;
    }

    private static IReadOnlyList<RoleGrant> ParseRoleGrants(JsonElement rolesArray)
    {
        var list = new List<RoleGrant>();
        foreach (var item in rolesArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (!item.TryGetProperty("role", out var roleEl) || !item.TryGetProperty("grant", out var grantEl))
                continue;
            var role = roleEl.GetString();
            var grant = grantEl.GetString();
            if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(grant))
                continue;
            try
            {
                list.Add(new RoleGrant(role.Trim(), grant.Trim()));
            }
            catch (ArgumentException)
            {
                // Skip invalid grant (e.g. unknown grant type)
            }
        }
        return list;
    }
}
