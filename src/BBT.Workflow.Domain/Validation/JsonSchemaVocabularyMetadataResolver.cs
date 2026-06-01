using System.Text.Json;

namespace BBT.Workflow.Validation;

internal sealed class JsonSchemaVocabularyMetadataResolver
{
    private const string PropertiesKey = "properties";
    private const string ItemsKey = "items";
    private const string LabelsKey = "x-labels";
    private const string LegacyLabelsKey = "labels";
    private const string ErrorMessagesKey = "x-errorMessages";
    private const string ValidationKey = "x-validation";

    private readonly Dictionary<string, JsonSchemaVocabularyField> _fields = new(StringComparer.Ordinal);

    private JsonSchemaVocabularyMetadataResolver(JsonElement schemaRoot)
    {
        Parse(schemaRoot, string.Empty);
    }

    public static JsonSchemaVocabularyMetadataResolver Resolve(JsonElement schemaRoot) => new(schemaRoot);

    public JsonSchemaVocabularyField? FindField(string path)
    {
        if (_fields.TryGetValue(path, out var field))
            return field;

        return _fields.TryGetValue(NormalizeArrayPath(path), out field) ? field : null;
    }

    public static string ResolveLocalizedText(JsonElement localizedText, string culture)
    {
        if (localizedText.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var requested = string.IsNullOrWhiteSpace(culture) ? "en-US" : culture.Trim();
        var neutral = requested.Split('-', 2)[0];

        if (TryGetString(localizedText, requested, out var exact))
            return exact;

        if (TryGetString(localizedText, neutral, out var neutralExact))
            return neutralExact;

        foreach (var item in localizedText.EnumerateObject())
        {
            if (item.Name.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase) &&
                item.Value.ValueKind == JsonValueKind.String)
            {
                return item.Value.GetString()!;
            }
        }

        if (TryGetString(localizedText, "en-US", out var english))
            return english;

        if (TryGetString(localizedText, "en", out var neutralEnglish))
            return neutralEnglish;

        foreach (var item in localizedText.EnumerateObject())
        {
            if (item.Value.ValueKind == JsonValueKind.String)
                return item.Value.GetString()!;
        }

        return string.Empty;
    }

    private void Parse(JsonElement node, string path)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (!string.IsNullOrEmpty(path))
        {
            _fields[path] = new JsonSchemaVocabularyField(path, node);
        }

        if (node.TryGetProperty(PropertiesKey, out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                Parse(property.Value, childPath);
            }
        }

        if (node.TryGetProperty(ItemsKey, out var items) && items.ValueKind == JsonValueKind.Object)
        {
            Parse(items, string.IsNullOrEmpty(path) ? "[]" : $"{path}[]");
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString()!;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string NormalizeArrayPath(string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], out _))
            {
                parts[i] = "[]";
            }
        }

        return string.Join('.', parts).Replace(".[].", "[].", StringComparison.Ordinal);
    }

    internal sealed class JsonSchemaVocabularyField(string path, JsonElement schemaNode)
    {
        public string Path { get; } = path;

        public JsonElement SchemaNode { get; } = schemaNode.Clone();

        public string? ResolveLabel(string culture)
        {
            if (SchemaNode.TryGetProperty(LabelsKey, out var labels))
            {
                var label = ResolveLocalizedText(labels, culture);
                if (!string.IsNullOrWhiteSpace(label))
                    return label;
            }

            if (SchemaNode.TryGetProperty(LegacyLabelsKey, out var legacyLabels) &&
                legacyLabels.ValueKind == JsonValueKind.Array)
            {
                return ResolveLegacyLabel(legacyLabels, culture);
            }

            return null;
        }

        public string? ResolveErrorMessage(string keyword, string culture)
        {
            if (!SchemaNode.TryGetProperty(ErrorMessagesKey, out var errorMessages) ||
                errorMessages.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(errorMessages, keyword, out var localizedText))
            {
                return null;
            }

            var message = ResolveLocalizedText(localizedText, culture);
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }

        public JsonElement? GetValidation()
        {
            return SchemaNode.TryGetProperty(ValidationKey, out var validation) &&
                   validation.ValueKind == JsonValueKind.Object
                ? validation.Clone()
                : null;
        }

        public IReadOnlyDictionary<string, JsonElement> GetKeywordParameters(string keyword)
        {
            var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (SchemaNode.TryGetProperty(keyword, out var value))
            {
                parameters[keyword] = value.Clone();
            }

            return parameters;
        }

        private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.Ordinal))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static string? ResolveLegacyLabel(JsonElement labels, string culture)
        {
            var requested = string.IsNullOrWhiteSpace(culture) ? "en-US" : culture.Trim();
            var neutral = requested.Split('-', 2)[0];
            string? first = null;
            string? english = null;
            string? neutralMatch = null;

            foreach (var item in labels.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("language", out var languageElement) ||
                    !item.TryGetProperty("label", out var labelElement) ||
                    languageElement.ValueKind != JsonValueKind.String ||
                    labelElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var language = languageElement.GetString();
                var label = labelElement.GetString();
                if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(label))
                    continue;

                first ??= label;

                if (language.Equals(requested, StringComparison.OrdinalIgnoreCase))
                    return label;

                if (language.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                    english ??= label;

                if (language.Equals(neutral, StringComparison.OrdinalIgnoreCase) ||
                    language.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase))
                {
                    neutralMatch ??= label;
                }
            }

            return neutralMatch ?? english ?? first;
        }
    }
}
