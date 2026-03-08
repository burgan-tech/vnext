using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Parses the "roles" vocabulary from a JSON Schema (master schema).
/// Extracts per-property role grants: path → RoleGrant[] for field-level visibility.
/// </summary>
public static class SchemaRolesParser
{
    private const string PropertiesKey = "properties";
    private const string RolesKey = "roles";

    /// <summary>
    /// Parses the schema and returns a map of property path to role grants.
    /// Path format: dot-separated (e.g. "amount", "internalNotes", "nested.field").
    /// Properties without "roles" are not included (treated as visible to all).
    /// </summary>
    /// <param name="schemaRoot">The root JsonElement of the schema (object with optional "properties").</param>
    /// <returns>Map of property path to list of role grants; empty if schema has no roles.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<RoleGrant>> ParsePropertyRoles(JsonElement schemaRoot)
    {
        var result = new Dictionary<string, IReadOnlyList<RoleGrant>>(StringComparer.Ordinal);
        if (schemaRoot.ValueKind != JsonValueKind.Object)
            return result;

        ParsePropertyRolesRecursive(schemaRoot, string.Empty, result);
        return result;
    }

    private static void ParsePropertyRolesRecursive(
        JsonElement node,
        string pathPrefix,
        Dictionary<string, IReadOnlyList<RoleGrant>> result)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (!node.TryGetProperty(PropertiesKey, out var properties) || properties.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in properties.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(pathPrefix) ? property.Name : $"{pathPrefix}.{property.Name}";
            var propValue = property.Value;

            if (propValue.TryGetProperty(RolesKey, out var rolesElement) && rolesElement.ValueKind == JsonValueKind.Array)
            {
                var grants = ParseRoleGrants(rolesElement);
                if (grants.Count > 0)
                    result[path] = grants;
            }

            if (propValue.ValueKind == JsonValueKind.Object && propValue.TryGetProperty(PropertiesKey, out _))
                ParsePropertyRolesRecursive(propValue, path, result);
        }
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
