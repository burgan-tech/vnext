using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Evaluates runtime <c>x-computed</c> fields over a data document and writes each result into the document.
/// <para>
/// Per field: the first case whose <c>when</c> condition holds wins, otherwise the <c>fallback</c>; each is a
/// <c>join</c> (the present, non-empty values of <c>fields</c> joined by <c>separator</c>, default a space) or an
/// <c>arithmetic</c> (<c>add</c> / <c>subtract</c> / <c>multiply</c> / <c>divide</c> over JSON numbers, left to right).
/// An unresolvable result — no case and no fallback, no value to join, a missing or non-numeric operand, a division by
/// zero, an overflow — becomes <c>""</c> (<c>whenAbsent: "empty"</c>, the default) or <c>null</c> (<c>"null"</c>).
/// </para>
/// <para>
/// Field and condition paths are relative to the object holding the computed property (dot-separated for nested
/// members). A computed field whose parent object is absent from the document is skipped, never created.
/// The evaluator only reads the document it is given: callers pass the caller's already-shaped view (after
/// <c>x-roles</c>, <c>x-masking</c>, <c>x-encryption</c>), so a computed value never reveals a field the caller cannot see.
/// </para>
/// </summary>
public static class ComputedFieldEvaluator
{
    /// <summary>
    /// Returns <paramref name="data"/> with every rule's result written in; <paramref name="data"/> itself when there is
    /// nothing to write (no rules, a non-object document, or no rule whose parent object exists).
    /// </summary>
    public static JsonElement Apply(JsonElement data, IReadOnlyList<ComputedFieldRule> rules)
    {
        if (rules.Count == 0 || data.ValueKind != JsonValueKind.Object)
            return data;

        // A fresh mutable copy: the input may be a cached or stored document shared with other readers.
        var root = JsonNode.Parse(data.GetRawText())!.AsObject();
        var written = false;
        foreach (var rule in rules)
        {
            if (Resolve(root, rule.ParentPath) is not JsonObject parent)
                continue;

            parent[rule.Name] = Evaluate(parent, rule.Declaration);
            written = true;
        }

        return written ? JsonSerializer.SerializeToElement(root) : data;
    }

    /// <summary>Evaluates one declaration against the object that holds the computed property.</summary>
    public static JsonNode? Evaluate(JsonObject parent, JsonElement declaration)
    {
        var whenAbsentNull = declaration.TryGetProperty("whenAbsent", out var whenAbsent) &&
                             whenAbsent.ValueKind == JsonValueKind.String && whenAbsent.GetString() == "null";

        JsonElement? action = null;
        if (declaration.TryGetProperty("cases", out var cases) && cases.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in cases.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object)
                    continue;
                if (candidate.TryGetProperty("when", out var when) && !Matches(parent, when))
                    continue;
                action = candidate;
                break;
            }
        }

        if (action is null && declaration.TryGetProperty("fallback", out var fallback) && fallback.ValueKind == JsonValueKind.Object)
            action = fallback;

        var result = action is { } chosen ? Compute(parent, chosen) : null;
        return result ?? (whenAbsentNull ? null : JsonValue.Create(string.Empty));
    }

    private static JsonNode? Compute(JsonObject parent, JsonElement action)
    {
        if (action.TryGetProperty("join", out var join) && join.ValueKind == JsonValueKind.Object)
            return Join(parent, join);
        if (action.TryGetProperty("arithmetic", out var arithmetic) && arithmetic.ValueKind == JsonValueKind.Object)
            return Arithmetic(parent, arithmetic);
        return null;
    }

    private static JsonNode? Join(JsonObject parent, JsonElement join)
    {
        var separator = join.TryGetProperty("separator", out var sep) && sep.ValueKind == JsonValueKind.String
            ? sep.GetString()!
            : " ";

        var parts = new List<string>();
        foreach (var field in Fields(join))
        {
            var text = AsText(Resolve(parent, field));
            if (!string.IsNullOrEmpty(text))
                parts.Add(text);
        }

        return parts.Count == 0 ? null : JsonValue.Create(string.Join(separator, parts));
    }

    private static JsonNode? Arithmetic(JsonObject parent, JsonElement arithmetic)
    {
        var op = arithmetic.TryGetProperty("op", out var opElement) && opElement.ValueKind == JsonValueKind.String
            ? opElement.GetString()
            : null;

        decimal? total = null;
        foreach (var field in Fields(arithmetic))
        {
            if (!TryNumber(Resolve(parent, field), out var operand))
                return null;

            if (total is not { } current)
            {
                total = operand;
                continue;
            }

            try
            {
                total = op switch
                {
                    "add" => current + operand,
                    "subtract" => current - operand,
                    "multiply" => current * operand,
                    "divide" when operand != 0m => current / operand,
                    _ => null
                };
            }
            catch (OverflowException)
            {
                return null;
            }

            if (total is null)
                return null;
        }

        return total is { } value ? JsonValue.Create(value) : null;
    }

    // ── conditions (vnext-schema view-vocab "condition") ─────────────────────

    private static bool Matches(JsonObject parent, JsonElement condition)
    {
        if (condition.ValueKind != JsonValueKind.Object)
            return false;

        if (condition.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
            return allOf.EnumerateArray().All(c => Matches(parent, c));
        if (condition.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
            return anyOf.EnumerateArray().Any(c => Matches(parent, c));
        if (condition.TryGetProperty("not", out var not))
            return !Matches(parent, not);

        if (!condition.TryGetProperty("field", out var fieldElement) || fieldElement.ValueKind != JsonValueKind.String ||
            !condition.TryGetProperty("operator", out var opElement) || opElement.ValueKind != JsonValueKind.String)
            return false;

        var actual = Resolve(parent, fieldElement.GetString()!);
        var hasValue = condition.TryGetProperty("value", out var expected);

        return opElement.GetString() switch
        {
            "isEmpty" => IsEmpty(actual),
            "isNotEmpty" => !IsEmpty(actual),
            "equals" => hasValue && JsonEquals(actual, expected),
            "notEquals" => !hasValue || !JsonEquals(actual, expected),
            "in" => hasValue && expected.ValueKind == JsonValueKind.Array &&
                    expected.EnumerateArray().Any(v => JsonEquals(actual, v)),
            "notIn" => !hasValue || expected.ValueKind != JsonValueKind.Array ||
                       !expected.EnumerateArray().Any(v => JsonEquals(actual, v)),
            "contains" => hasValue && Contains(actual, expected),
            "startsWith" => hasValue && TextOf(actual, expected) is var (a, e) && a is not null && a.StartsWith(e!, StringComparison.Ordinal),
            "endsWith" => hasValue && TextOf(actual, expected) is var (a2, e2) && a2 is not null && a2.EndsWith(e2!, StringComparison.Ordinal),
            "greaterThan" => hasValue && Compare(actual, expected) is > 0,
            "lessThan" => hasValue && Compare(actual, expected) is < 0,
            "greaterThanOrEquals" => hasValue && Compare(actual, expected) is >= 0,
            "lessThanOrEquals" => hasValue && Compare(actual, expected) is <= 0,
            _ => false
        };
    }

    private static bool JsonEquals(JsonNode? actual, JsonElement expected)
    {
        if (actual is null)
            return expected.ValueKind == JsonValueKind.Null;

        if (TryNumber(actual, out var left) && expected.ValueKind == JsonValueKind.Number &&
            expected.TryGetDecimal(out var right))
            return left == right;

        return JsonNode.DeepEquals(actual, JsonNode.Parse(expected.GetRawText()));
    }

    private static bool Contains(JsonNode? actual, JsonElement expected)
    {
        if (actual is JsonArray array)
            return array.Any(item => JsonEquals(item, expected));

        var (text, needle) = TextOf(actual, expected);
        return text is not null && text.Contains(needle!, StringComparison.Ordinal);
    }

    private static (string? Actual, string? Expected) TextOf(JsonNode? actual, JsonElement expected)
        => actual is JsonValue value && value.GetValueKind() == JsonValueKind.String &&
           expected.ValueKind == JsonValueKind.String
            ? (value.GetValue<string>(), expected.GetString())
            : (null, null);

    /// <summary>Numbers compare numerically, strings ordinally (ISO dates order correctly); anything else does not compare.</summary>
    private static int? Compare(JsonNode? actual, JsonElement expected)
    {
        if (TryNumber(actual, out var left) && expected.ValueKind == JsonValueKind.Number &&
            expected.TryGetDecimal(out var right))
            return left.CompareTo(right);

        var (a, e) = TextOf(actual, expected);
        return a is null ? null : string.CompareOrdinal(a, e);
    }

    // ── values ───────────────────────────────────────────────────────────────

    private static IEnumerable<string> Fields(JsonElement action)
        => action.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array
            ? fields.EnumerateArray()
                .Where(f => f.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(f.GetString()))
                .Select(f => f.GetString()!)
            : [];

    private static JsonNode? Resolve(JsonObject root, string path)
    {
        if (path.Length == 0)
            return root;

        JsonNode? current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current))
                return null;
        }

        return current;
    }

    private static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonObject obj => obj.Count == 0,
        JsonValue value => value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Length == 0,
        _ => false
    };

    private static string? AsText(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool TryNumber(JsonNode? node, out decimal number)
    {
        number = 0;
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.Number &&
               decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
}
