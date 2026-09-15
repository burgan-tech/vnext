using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Definitions;

public static class AttributeSqlExpression
{
    public static string Resolve(string field, string storageType, string fallback, SchemaFilterContext? context)
    {
        // Callers pass a relative JSON path. "attributes" can itself be a real property name;
        // stripping the API prefix twice would route that property to a different projection.
        var path = field;
        var metadata = context?.GetFieldMetadata(path);
        if (metadata?.Indexed != true) return fallback;
        if (storageType == "numeric" && metadata.Type is not ("number" or "integer")) return fallback;
        if (storageType == "timestamptz" && (metadata.Type != "string" || metadata.Format != "date-time")) return fallback;
        var definition = new AttributeIndexDefinition(path, storageType);
        return context!.ReadyIndexes.Contains(definition.Key) ? $"\"{definition.ColumnName}\"" : fallback;
    }
}
