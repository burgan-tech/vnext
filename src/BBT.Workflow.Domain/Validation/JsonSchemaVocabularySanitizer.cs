using System.Text.Json;
using System.Text.Json.Nodes;

namespace BBT.Workflow.Validation;

internal static class JsonSchemaVocabularySanitizer
{
    private static readonly string[] SchemaMapKeywords =
    [
        "$defs",
        "definitions",
        "dependentSchemas",
        "patternProperties",
        "properties"
    ];

    private static readonly string[] VocabularyKeywords =
    [
        "labels",
        "x-labels",
        "x-errorMessages",
        "x-enum",
        "x-validation",
        "x-sensitive"
    ];

    public static JsonElement RemoveVocabularyKeywords(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        if (node is null)
        {
            return schema.Clone();
        }

        RemoveVocabularyKeywords(node, isSchemaMap: false);

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void RemoveVocabularyKeywords(JsonNode node, bool isSchemaMap)
    {
        if (node is JsonObject obj)
        {
            if (!isSchemaMap)
            {
                foreach (var keyword in VocabularyKeywords)
                {
                    obj.Remove(keyword);
                }
            }

            foreach (var property in obj.ToList())
            {
                if (property.Value is not null)
                {
                    RemoveVocabularyKeywords(
                        property.Value,
                        property.Value is JsonObject && IsSchemaMapKeyword(property.Key));
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    RemoveVocabularyKeywords(item, isSchemaMap: false);
                }
            }
        }
    }

    private static bool IsSchemaMapKeyword(string keyword)
        => SchemaMapKeywords.Any(item => item.Equals(keyword, StringComparison.Ordinal));
}
