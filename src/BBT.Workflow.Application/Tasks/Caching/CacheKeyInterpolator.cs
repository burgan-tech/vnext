using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Caching;

/// <summary>
/// Resolves a cache-key template by substituting <c>{context.&lt;path&gt;}</c> placeholders with values
/// read from the <see cref="ScriptContext"/>. Paths use dot-notation over the context namespace, e.g.
/// <c>{context.Headers.customerId}</c>, <c>{context.Instance.Data.customer.id}</c>,
/// <c>{context.Body.accountId}</c>. Resolution reuses <see cref="ContextPathResolver"/> semantics
/// (case-insensitive, never throws on missing navigation).
/// </summary>
public static partial class CacheKeyInterpolator
{
    private const string ContextPrefix = "context.";

    [GeneratedRegex(@"\{([^{}]+)\}", RegexOptions.Compiled)]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Interpolates the <paramref name="template"/> against the <paramref name="context"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a placeholder cannot be resolved to a value, so a mis-resolved key never silently
    /// collides with another entry.
    /// </exception>
    public static string Interpolate(string template, ScriptContext context)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('{'))
        {
            return template;
        }

        var root = BuildContextRoot(context);

        return TokenPattern().Replace(template, match =>
        {
            var expression = match.Groups[1].Value.Trim();
            var path = expression.StartsWith(ContextPrefix, StringComparison.OrdinalIgnoreCase)
                ? expression[ContextPrefix.Length..]
                : expression;

            var values = ContextPathResolver.Resolve(root, path);
            if (values.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Cache key placeholder '{{{expression}}}' could not be resolved from the script context.");
            }

            return values[0];
        });
    }

    /// <summary>
    /// Builds a JSON root whose shape mirrors the <c>context.*</c> namespace used in key templates.
    /// </summary>
    private static JsonElement BuildContextRoot(ScriptContext context)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            WriteDictionary(writer, "Headers", context.GetHeadersAsDictionary());
            WriteDictionary(writer, "RouteValues", context.GetRouteValuesAsDictionary());

            writer.WritePropertyName("Body");
            context.GetBodyAsJsonElement().WriteTo(writer);

            writer.WritePropertyName("QueryParameters");
            WriteDynamicAsElement(writer, context.QueryParameters);

            writer.WritePropertyName("Instance");
            WriteInstance(writer, context);

            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<JsonElement>(buffer.ToArray());
    }

    private static void WriteDictionary(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        foreach (var kvp in values)
        {
            writer.WriteString(kvp.Key, kvp.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteInstance(Utf8JsonWriter writer, ScriptContext context)
    {
        var instance = context.Instance;
        if (instance is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("Id", instance.Id.ToString());
        writer.WriteString("Key", instance.Key);
        writer.WriteString("Flow", instance.Flow);
        writer.WriteString("Status", instance.Status.ToString());
        writer.WriteString("CurrentState", instance.CurrentState);

        writer.WritePropertyName("Data");
        var data = instance.LatestData?.Data.JsonElement;
        if (data is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) })
        {
            data.Value.WriteTo(writer);
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteDynamicAsElement(Utf8JsonWriter writer, dynamic? value)
    {
        if (value is null)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(value);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.WriteTo(writer);
        }
        catch (Exception)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }
}
