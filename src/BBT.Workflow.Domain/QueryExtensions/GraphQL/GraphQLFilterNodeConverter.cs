using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions.GraphQL;

/// <summary>
/// Custom JSON converter for GraphQLFilterNode to handle the complex nested structure
/// Supports logical operators (and, or, not) and field conditions (attributes)
/// </summary>
public sealed class GraphQLFilterNodeConverter : JsonConverter<GraphQLFilterNode>
{
    public override GraphQLFilterNode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected start of object for GraphQLFilterNode");

        var node = new GraphQLFilterNode();
        
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name");

            var originalPropertyName = reader.GetString();
            var propertyName = originalPropertyName?.ToLowerInvariant();
            reader.Read();

            switch (propertyName)
            {
                case "and":
                    node.And = ReadFilterNodeArray(ref reader, options);
                    break;
                case "or":
                    node.Or = ReadFilterNodeArray(ref reader, options);
                    break;
                case "not":
                    node.Not = Read(ref reader, typeToConvert, options);
                    break;
                case "attributes":
                    // Parse attributes and merge into existing Attributes dictionary
                    var attributesDict = ReadFieldConditions(ref reader, options);
                    if (attributesDict != null)
                    {
                        // Initialize Attributes if null
                        node.Attributes ??= new Dictionary<string, FieldCondition>();
                        
                        // Merge attributes into node.Attributes
                        foreach (var kvp in attributesDict)
                        {
                            node.Attributes[kvp.Key] = kvp.Value;
                        }
                    }
                    break;
                default:
                    // Handle root-level field conditions (e.g., {"key": {"eq": "1111"}})
                    // This supports Instance columns without "attributes" wrapper
                    if (reader.TokenType == JsonTokenType.StartObject && originalPropertyName != null)
                    {
                        // Initialize Attributes if null
                        node.Attributes ??= new Dictionary<string, FieldCondition>();
                        
                        // Read the field condition - use original property name to preserve casing
                        var fieldCondition = ReadFieldCondition(ref reader, originalPropertyName, options);
                        node.Attributes[originalPropertyName] = fieldCondition;
                    }
                    else
                    {
                        // Skip unknown/unsupported properties
                        reader.Skip();
                    }
                    break;
            }
        }

        return node;
    }

    private static List<GraphQLFilterNode>? ReadFilterNodeArray(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected array for logical operator");

        var list = new List<GraphQLFilterNode>();
        var converter = new GraphQLFilterNodeConverter();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                break;

            var node = converter.Read(ref reader, typeof(GraphQLFilterNode), options);
            if (node != null)
                list.Add(node);
        }

        return list;
    }

    private static Dictionary<string, FieldCondition>? ReadFieldConditions(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected object for attributes");

        var dict = new Dictionary<string, FieldCondition>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name for field");

            var fieldName = reader.GetString() ?? throw new JsonException("Field name cannot be null");
            reader.Read();

            var condition = ReadFieldCondition(ref reader, fieldName, options);
            dict[fieldName] = condition;
        }

        return dict;
    }

    private static FieldCondition ReadFieldCondition(ref Utf8JsonReader reader, string fieldPath, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected object for field condition: {fieldPath}");

        var condition = new FieldCondition();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name for operator");

            var operatorName = reader.GetString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(operatorName))
            {
                reader.Skip();
                continue;
            }

            reader.Read();

            switch (operatorName)
            {
                case "eq":
                    condition.Eq = ReadValue(ref reader);
                    break;
                case "ne":
                    condition.Ne = ReadValue(ref reader);
                    break;
                case "gt":
                    condition.Gt = ReadValue(ref reader);
                    break;
                case "ge":
                    condition.Ge = ReadValue(ref reader);
                    break;
                case "lt":
                    condition.Lt = ReadValue(ref reader);
                    break;
                case "le":
                    condition.Le = ReadValue(ref reader);
                    break;
                case "between":
                    condition.Between = ReadValueArray(ref reader);
                    break;
                case "like":
                    condition.Like = reader.GetString();
                    break;
                case "match":
                    condition.Match = reader.GetString();
                    break;
                case "startswith":
                    condition.StartsWith = reader.GetString();
                    break;
                case "endswith":
                    condition.EndsWith = reader.GetString();
                    break;
                case "in":
                    condition.In = ReadValueArray(ref reader);
                    break;
                case "nin":
                    condition.NotIn = ReadValueArray(ref reader);
                    break;
                case "isnull":
                    condition.IsNull = reader.GetBoolean();
                    break;
                default:
                    // This could be a nested field - handle as nested condition
                    // e.g., {"parent": {"child": {"eq": "value"}}}
                    if (reader.TokenType == JsonTokenType.StartObject && !string.IsNullOrEmpty(operatorName))
                    {
                        condition.NestedConditions ??= new Dictionary<string, object>();
                        var nestedJson = JsonDocument.ParseValue(ref reader);
                        condition.NestedConditions[operatorName] = nestedJson.RootElement.Clone();
                    }
                    else
                    {
                        reader.Skip();
                    }
                    break;
            }
        }

        return condition;
    }

    private static object? ReadValue(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt64(out var l) => l,
            JsonTokenType.Number when reader.TryGetDecimal(out var d) => d,
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token type: {reader.TokenType}")
        };
    }

    private static object[]? ReadValueArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected array for value list");

        var list = new List<object>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                break;

            var value = ReadValue(ref reader);
            if (value != null)
                list.Add(value);
        }

        return list.ToArray();
    }

    public override void Write(Utf8JsonWriter writer, GraphQLFilterNode value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.And != null && value.And.Count > 0)
        {
            writer.WritePropertyName("and");
            writer.WriteStartArray();
            foreach (var node in value.And)
            {
                Write(writer, node, options);
            }
            writer.WriteEndArray();
        }

        if (value.Or != null && value.Or.Count > 0)
        {
            writer.WritePropertyName("or");
            writer.WriteStartArray();
            foreach (var node in value.Or)
            {
                Write(writer, node, options);
            }
            writer.WriteEndArray();
        }

        if (value.Not != null)
        {
            writer.WritePropertyName("not");
            Write(writer, value.Not, options);
        }

        if (value.Attributes != null && value.Attributes.Count > 0)
        {
            writer.WritePropertyName("attributes");
            JsonSerializer.Serialize(writer, value.Attributes, options);
        }

        writer.WriteEndObject();
    }
}


