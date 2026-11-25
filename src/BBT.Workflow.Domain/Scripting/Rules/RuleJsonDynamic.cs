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

    // True for a Borrow view: the element is not cloned, and every nested value it hands out borrows too.
    private readonly bool _borrowed;

    // Set for a BorrowObject view: an object whose top-level members are resolved on demand; the whole object is
    // materialized only when the view is used as a value (ToString, AsJsonElement, conversion).
    private readonly IRuleJsonObjectSource? _source;

    private RuleJsonDynamic(JsonElement element, bool borrowed = false)
    {
        _element = borrowed ? element : element.Clone();
        _borrowed = borrowed;
    }

    private RuleJsonDynamic(IRuleJsonObjectSource source)
    {
        _source = source;
        _borrowed = true;
    }

    // The element this view stands for; for a BorrowObject view the source's materialized object.
    private JsonElement Element => _source is null ? _element : _source.Materialize();

    // The value kind without materializing a BorrowObject view (always an object).
    private JsonValueKind Kind => _source is null ? _element.ValueKind : JsonValueKind.Object;

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
    /// Builds a view over <paramref name="element"/> WITHOUT cloning it; nested values reached through the
    /// view are borrowed the same way. For one expression-evaluation scope only: the view (and anything read
    /// through it) is valid only while the <see cref="JsonDocument"/> that owns the element is alive — after
    /// that document is disposed every read throws <see cref="ObjectDisposedException"/>. Never store a
    /// borrowed view beyond the scope; use <see cref="FromJsonElement"/> when the value must outlive it.
    /// A borrowed view is also lenient where a legacy view fails: a member of a non-object, or an index that is out
    /// of range or of the wrong kind, reads as missing (<c>null</c>) — a declarative mapping's missing source writes
    /// nothing (RF-1). A call of a method the view does not declare (<c>input.name.Substring(1)</c>) is not lenient: it
    /// throws <see cref="InvalidOperationException"/> telling the author to convert first (<c>.ToString()</c>).
    /// Views from <see cref="FromJsonElement"/> / <see cref="FromObject"/> keep failing as before.
    /// </summary>
    public static RuleJsonDynamic Borrow(JsonElement element) => new(element, borrowed: true);

    /// <summary>
    /// A <see cref="Borrow"/> view of a JSON object whose top-level members <paramref name="source"/> resolves on
    /// demand: a member read (<c>result.body.x</c>) walks only that member's element, and the whole object is built by
    /// <see cref="IRuleJsonObjectSource.Materialize"/> only when the view itself is used as a value. Reads behave
    /// exactly as a <see cref="Borrow"/> view over the materialized object would; the same lifetime rule applies.
    /// </summary>
    public static RuleJsonDynamic BorrowObject(IRuleJsonObjectSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new RuleJsonDynamic(source);
    }

    /// <summary>
    /// Returns the underlying (read-only; cloned unless this view was made by <see cref="Borrow"/>) <see cref="JsonElement"/> for callers that need to
    /// enumerate object properties or array items — operations the dynamic surface does not expose.
    /// </summary>
    public JsonElement AsJsonElement() => Element;

    /// <summary>
    /// Numeric JSON value as <see cref="double"/> for use in expressions (e.g. comparisons).
    /// </summary>
    public double AsDouble() =>
        Kind == JsonValueKind.Number
            ? _element.GetDouble()
            : throw new InvalidOperationException("JSON value is not a number.");

    /// <summary>
    /// Numeric JSON value as <see cref="int"/> for use in expressions.
    /// </summary>
    public int AsInt32() =>
        Kind == JsonValueKind.Number
            ? _element.GetInt32()
            : throw new InvalidOperationException("JSON value is not a number.");

    /// <summary>
    /// JSON array length; use in expressions instead of relying on dynamic <c>Count</c>.
    /// </summary>
    public int AsArrayLength() =>
        Kind == JsonValueKind.Array
            ? _element.GetArrayLength()
            : throw new InvalidOperationException("JSON value is not an array.");

    /// <summary>
    /// JSON boolean (or string-parseable boolean).
    /// </summary>
    public bool AsBoolean() =>
        Kind switch
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
    public override string ToString()
    {
        var element = Element;
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => element.GetRawText(),
            JsonValueKind.False => element.GetRawText(),
            _ => element.GetRawText()
        };
    }

    /// <inheritdoc />
    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        result = null;

        if (binder.Name == "Count" && Kind == JsonValueKind.Array)
        {
            result = _element.GetArrayLength();
            return true;
        }

        if (Kind != JsonValueKind.Object)
            return _borrowed; // a Borrow (declarative scope) view reads a member of a non-object as missing (null)

        if (TryGetProperty(binder.Name, out var prop))
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

        if (indexes[0] is string key && Kind == JsonValueKind.Object)
        {
            if (TryGetProperty(key, out var prop))
            {
                result = Wrap(prop);
                return true;
            }

            // Missing key: succeed with null (same semantics as TryGetMember for unknown properties).
            return true;
        }

        if (indexes[0] is int idx && Kind == JsonValueKind.Array)
        {
            if (idx < 0 || idx >= _element.GetArrayLength())
                return _borrowed;
            result = Wrap(_element[idx]);
            return true;
        }

        // A Borrow (declarative scope) view reads an index that does not apply as missing (null); legacy views fail.
        return _borrowed;
    }

    /// <inheritdoc />
    public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
    {
        result = null;
        args ??= Array.Empty<object?>();

        if (Kind != JsonValueKind.Array || !string.Equals(binder.Name, "Contains", StringComparison.OrdinalIgnoreCase) ||
            args.Length != 1)
        {
            // A Borrow (declarative scope) view fails here: returning false would fall back to a member read, which a
            // borrowed view answers with null, and invoking null reads as a missing source — the call silently skipped.
            // The declared CLR methods (AsDouble, ToString, …) bind before this runs; legacy views keep the binder error.
            if (_borrowed)
                throw new InvalidOperationException(
                    $"'{binder.Name}' is not a method of this JSON {Kind} value; convert it first, e.g. .ToString().{binder.Name}(...)");
            return base.TryInvokeMember(binder, args, out result);
        }

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
        var element = Element; // a BorrowObject view converts its materialized object

        try
        {
            if (binder.Type == typeof(bool))
            {
                if (element.ValueKind == JsonValueKind.True)
                {
                    result = true;
                    return true;
                }

                if (element.ValueKind == JsonValueKind.False)
                {
                    result = false;
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var b))
                {
                    result = b;
                    return true;
                }
            }

            if (binder.Type == typeof(double))
            {
                if (element.ValueKind == JsonValueKind.Number)
                {
                    result = element.GetDouble();
                    return true;
                }
            }

            if (binder.Type == typeof(decimal))
            {
                if (element.ValueKind == JsonValueKind.Number)
                {
                    result = element.GetDecimal();
                    return true;
                }
            }

            if (binder.Type == typeof(long))
            {
                if (element.ValueKind == JsonValueKind.Number)
                {
                    result = element.GetInt64();
                    return true;
                }
            }

            if (binder.Type == typeof(int))
            {
                if (element.ValueKind == JsonValueKind.Number)
                {
                    result = element.GetInt32();
                    return true;
                }
            }

            if (binder.Type == typeof(string))
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    result = element.GetString();
                    return true;
                }

                if (element.ValueKind == JsonValueKind.Null)
                {
                    result = null;
                    return true;
                }

                result = element.GetRawText();
                return true;
            }
        }
        catch
        {
            return base.TryConvert(binder, out result);
        }

        return base.TryConvert(binder, out result);
    }

    // A member of this (object) view: exact name first, then the first case-insensitive match in member order.
    private bool TryGetProperty(string name, out JsonElement value)
    {
        if (_source is null)
            return TryGetPropertyIgnoreCase(_element, name, out value);

        var names = _source.MemberNames;
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                value = _source.GetMember(i);
                return true;
            }
        }

        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                value = _source.GetMember(i);
                return true;
            }
        }

        value = default;
        return false;
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

    private object? Wrap(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => new RuleJsonDynamic(e, _borrowed)
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

/// <summary>
/// Supplies a <see cref="RuleJsonDynamic.BorrowObject"/> view: a JSON object with a fixed list of top-level members,
/// each resolved on demand, and the whole object only when it is used as a value. Elements are borrowed — the source
/// owns (and eventually disposes) whatever documents back them.
/// </summary>
public interface IRuleJsonObjectSource
{
    /// <summary>The object's member names, in document order (distinct, ordinal).</summary>
    IReadOnlyList<string> MemberNames { get; }

    /// <summary>The member at <paramref name="index"/> of <see cref="MemberNames"/>; JSON null as a <see cref="JsonValueKind.Null"/> element.</summary>
    JsonElement GetMember(int index);

    /// <summary>The whole object, built once on first call.</summary>
    JsonElement Materialize();
}
