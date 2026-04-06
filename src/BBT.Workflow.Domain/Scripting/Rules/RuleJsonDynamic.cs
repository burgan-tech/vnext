using System.Dynamic;
using System.Text.Json;

namespace BBT.Workflow.Scripting.Rules;

/// <summary>
/// Read-only JSON value surface for rule expressions: supports member access, indexers,
/// <c>Count</c> on arrays, <c>Contains</c> on arrays, conversion to primitives, and
/// <see cref="ToString"/> for string comparisons in expressions (e.g. Dynamic Expresso).
/// </summary>
public sealed class RuleJsonDynamic : DynamicObject
{
    private readonly JsonElement _element;

    private RuleJsonDynamic(JsonElement element)
    {
        _element = element.Clone();
    }

    /// <summary>
    /// Empty JSON object <c>{}</c>.
    /// </summary>
    public static RuleJsonDynamic Empty { get; } = CreateEmpty();

    private static RuleJsonDynamic CreateEmpty()
    {
        using var doc = JsonDocument.Parse("{}");
        return new RuleJsonDynamic(doc.RootElement);
    }

    /// <summary>
    /// Builds a view from a <see cref="JsonElement"/> (cloned).
    /// </summary>
    public static RuleJsonDynamic FromJsonElement(JsonElement element) => new(element);

    /// <summary>
    /// Numeric JSON value as <see cref="double"/> for use in expressions (e.g. comparisons).
    /// </summary>
    public double AsDouble() =>
        _element.ValueKind == JsonValueKind.Number
            ? _element.GetDouble()
            : throw new InvalidOperationException("JSON value is not a number.");

    /// <summary>
    /// Numeric JSON value as <see cref="int"/> for use in expressions.
    /// </summary>
    public int AsInt32() =>
        _element.ValueKind == JsonValueKind.Number
            ? _element.GetInt32()
            : throw new InvalidOperationException("JSON value is not a number.");

    /// <summary>
    /// JSON array length; use in expressions instead of relying on dynamic <c>Count</c>.
    /// </summary>
    public int AsArrayLength() =>
        _element.ValueKind == JsonValueKind.Array
            ? _element.GetArrayLength()
            : throw new InvalidOperationException("JSON value is not an array.");

    /// <summary>
    /// JSON boolean (or string-parseable boolean).
    /// </summary>
    public bool AsBoolean() =>
        _element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(_element.GetString(), out var b) => b,
            _ => throw new InvalidOperationException("JSON value is not a boolean.")
        };

    /// <summary>
    /// Serializes a value to JSON and wraps the root as <see cref="RuleJsonDynamic"/>.
    /// </summary>
    public static RuleJsonDynamic FromObject(object? value)
    {
        if (value is null)
            return Empty;

        var json = JsonSerializer.Serialize(value, global::BBT.Workflow.JsonSerializerConstants.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        return new RuleJsonDynamic(doc.RootElement);
    }

    /// <summary>
    /// Returns a form suitable for rule comparisons: JSON strings as their text (no quotes),
    /// numbers and booleans as in <see cref="JsonElement.GetRawText"/>, objects and arrays as compact JSON.
    /// </summary>
    public override string ToString() =>
        _element.ValueKind switch
        {
            JsonValueKind.String => _element.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Number => _element.GetRawText(),
            JsonValueKind.True => _element.GetRawText(),
            JsonValueKind.False => _element.GetRawText(),
            _ => _element.GetRawText()
        };

    /// <inheritdoc />
    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        result = null;

        if (binder.Name == "Count" && _element.ValueKind == JsonValueKind.Array)
        {
            result = _element.GetArrayLength();
            return true;
        }

        if (_element.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetPropertyIgnoreCase(_element, binder.Name, out var prop))
        {
            result = Wrap(prop);
            return true;
        }

        // Missing property: succeed with null so callers (e.g. Dynamic Expresso) can use ?. / ?? without binder failure.
        return true;
    }

    /// <inheritdoc />
    public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object? result)
    {
        result = null;
        if (indexes.Length != 1)
            return false;

        if (indexes[0] is string key && _element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(_element, key, out var prop))
            {
                result = Wrap(prop);
                return true;
            }

            // Missing key: succeed with null (same semantics as TryGetMember for unknown properties).
            return true;
        }

        if (indexes[0] is int idx && _element.ValueKind == JsonValueKind.Array)
        {
            if (idx < 0 || idx >= _element.GetArrayLength())
                return false;
            result = Wrap(_element[idx]);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
    {
        result = null;
        args ??= Array.Empty<object?>();

        if (_element.ValueKind != JsonValueKind.Array || !string.Equals(binder.Name, "Contains", StringComparison.OrdinalIgnoreCase) ||
            args.Length != 1)
            return base.TryInvokeMember(binder, args, out result);

        var needle = args[0];
        foreach (var item in _element.EnumerateArray())
        {
            if (JsonElementMatches(item, needle))
            {
                result = true;
                return true;
            }
        }

        result = false;
        return true;
    }

    /// <inheritdoc />
    public override bool TryConvert(ConvertBinder binder, out object? result)
    {
        result = null;

        try
        {
            if (binder.Type == typeof(bool))
            {
                if (_element.ValueKind == JsonValueKind.True)
                {
                    result = true;
                    return true;
                }

                if (_element.ValueKind == JsonValueKind.False)
                {
                    result = false;
                    return true;
                }

                if (_element.ValueKind == JsonValueKind.String && bool.TryParse(_element.GetString(), out var b))
                {
                    result = b;
                    return true;
                }
            }

            if (binder.Type == typeof(double))
            {
                if (_element.ValueKind == JsonValueKind.Number)
                {
                    result = _element.GetDouble();
                    return true;
                }
            }

            if (binder.Type == typeof(decimal))
            {
                if (_element.ValueKind == JsonValueKind.Number)
                {
                    result = _element.GetDecimal();
                    return true;
                }
            }

            if (binder.Type == typeof(long))
            {
                if (_element.ValueKind == JsonValueKind.Number)
                {
                    result = _element.GetInt64();
                    return true;
                }
            }

            if (binder.Type == typeof(int))
            {
                if (_element.ValueKind == JsonValueKind.Number)
                {
                    result = _element.GetInt32();
                    return true;
                }
            }

            if (binder.Type == typeof(string))
            {
                if (_element.ValueKind == JsonValueKind.String)
                {
                    result = _element.GetString();
                    return true;
                }

                if (_element.ValueKind == JsonValueKind.Null)
                {
                    result = null;
                    return true;
                }

                result = _element.GetRawText();
                return true;
            }
        }
        catch
        {
            return base.TryConvert(binder, out result);
        }

        return base.TryConvert(binder, out result);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;

        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static object? Wrap(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => new RuleJsonDynamic(e)
        };
    }

    private static bool JsonElementMatches(JsonElement haystack, object? needle)
    {
        return haystack.ValueKind switch
        {
            JsonValueKind.String => needle is string s && haystack.GetString() == s,
            JsonValueKind.Number => needle is IConvertible && CompareNumber(haystack, needle),
            JsonValueKind.True => needle is true,
            JsonValueKind.False => needle is false,
            _ => string.Equals(haystack.GetRawText(),
                JsonSerializer.Serialize(needle, global::BBT.Workflow.JsonSerializerConstants.JsonOptions),
                StringComparison.Ordinal)
        };
    }

    private static bool CompareNumber(JsonElement element, object needle)
    {
        try
        {
            var d = element.GetDouble();
            return needle switch
            {
                int i => Math.Abs(d - i) < double.Epsilon,
                long l => Math.Abs(d - l) < double.Epsilon,
                double x => Math.Abs(d - x) < double.Epsilon,
                decimal m => Math.Abs((decimal)d - m) < 0.0000001m,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }
}
