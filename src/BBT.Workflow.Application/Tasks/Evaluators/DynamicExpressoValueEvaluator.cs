using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Rules;
using DynamicExpresso;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Evaluators;

/// <summary>
/// Evaluates a Dynamic Expresso expression (a <see cref="ScriptCode"/> with
/// <c>location = "dynamicExpresso"</c>) to a <see cref="string"/> against the allowlisted
/// <see cref="ExpressoRuleContext"/>. Used to compute a cache key from the request/script context
/// without a full Roslyn <c>.csx</c> mapping — the same interpreter the condition rules use.
/// </summary>
public interface IDynamicExpressoValueEvaluator
{
    /// <summary>
    /// Evaluates the expression to a string. Fails when the script is not a Dynamic Expresso expression,
    /// is empty/too long, cannot be decoded, or throws during evaluation.
    /// </summary>
    Result<string> Evaluate(ScriptCode script, ScriptContext context);
}

/// <inheritdoc />
public sealed class DynamicExpressoValueEvaluator(ILogger<DynamicExpressoValueEvaluator> logger)
    : IDynamicExpressoValueEvaluator
{
    private static readonly ConcurrentDictionary<string, Func<ExpressoRuleContext, string>> CompiledExpressions =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Result<string> Evaluate(ScriptCode script, ScriptContext context)
    {
        if (!ConditionScriptLocations.IsDynamicExpresso(script.Location))
        {
            return Result<string>.Fail(Error.Validation(
                WorkflowErrorCodes.TaskExecution,
                "Script location is not configured for Dynamic Expresso evaluation."));
        }

        string expression;
        try
        {
            expression = script.DecodedCode.Trim();
        }
        catch (InvalidOperationException ex)
        {
            return Result<string>.Fail(Error.Validation(
                WorkflowErrorCodes.TaskExecution,
                $"Expresso expression could not be decoded: {ex.Message}"));
        }

        if (string.IsNullOrWhiteSpace(expression))
        {
            return Result<string>.Fail(Error.Validation(
                WorkflowErrorCodes.TaskExecution,
                "Dynamic Expresso expression is empty."));
        }

        if (expression.Length > ConditionScriptLocations.MaxDynamicExpressoExpressionLength)
        {
            return Result<string>.Fail(Error.Validation(
                WorkflowErrorCodes.TaskExecution,
                $"Dynamic Expresso expression exceeds maximum length ({ConditionScriptLocations.MaxDynamicExpressoExpressionLength})."));
        }

        try
        {
            var ruleContext = ExpressoRuleContextMapper.FromScriptContext(context);
            var fn = CompiledExpressions.GetOrAdd(expression, CompileExpression);
            var value = fn(ruleContext);
            return Result<string>.Ok(value);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Dynamic Expresso value evaluation failed: {Error}", ex.Message);
            return Result<string>.Fail(Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"Dynamic Expresso evaluation failed: {ex.Message}"));
        }
    }

    private static Func<ExpressoRuleContext, string> CompileExpression(string expression)
    {
        var interpreter = new Interpreter(InterpreterOptions.Default);
        // Deterministic hash helper for building bounded, vary-by-correct cache keys, e.g.
        // "cfg:" + context.Instance.Key + ":" + sha256(varyKey(context)).
        interpreter.SetFunction("sha256", (Func<string?, string>)Sha256Hex);
        // Canonical vary-by string builder (see VaryKey). Because DynamicExpresso cannot express list
        // operations (no LINQ / array literals), the header-name set is assembled in C# here.
        interpreter.SetFunction("varyKey", (Func<ExpressoRuleContext, string>)VaryKey);
        var lambda = interpreter.Parse(expression, typeof(string), new Parameter("context", typeof(ExpressoRuleContext)));
        return lambda.Compile<Func<ExpressoRuleContext, string>>();
    }

    /// <summary>
    /// Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="input"/> (empty string for null).
    /// </summary>
    private static string Sha256Hex(string? input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>MetaData key carrying the exact vary-by header names (domain-supplied cache config).</summary>
    public const string VaryByHeadersMetadataKey = "__cacheVaryByHeaders";

    /// <summary>MetaData key carrying the vary-by header-name prefixes (domain-supplied cache config).</summary>
    public const string VaryByPrefixesMetadataKey = "__cacheVaryByHeaderPrefixes";

    private const string VaryByInstanceDataKey = "varyBy";
    private const string VersionQueryParam = "version";
    private const string DefaultVersion = "latest";

    /// <summary>
    /// Builds a canonical, UNhashed vary-by string from exactly the request inputs that can change the
    /// result. Header-name set resolution (most-specific first):
    /// <list type="number">
    ///   <item><c>context.Instance.Data["varyBy"]</c> when it is a non-empty string array (per-request,
    ///   most precise; the domain populates it with the headers the linked feature-flag actually reads);</item>
    ///   <item>otherwise the domain-supplied cache config — exact <c>VaryByHeaders</c> unioned with all
    ///   request headers matching <c>VaryByHeaderPrefixes</c> (carried in MetaData);</item>
    ///   <item>otherwise the last-resort superset: every request header (never under-keys).</item>
    /// </list>
    /// Then canonicalizes: names lowercased/trimmed, de-duplicated, ordinal-sorted; each value read
    /// case-insensitively (missing → empty); joined as <c>name=value</c> with <c>|</c>; the version
    /// selector (query param <c>version</c>, default <c>latest</c>) is prepended as <c>v=&lt;version&gt;|…</c>.
    /// The result is intended to be hashed by the caller via <c>sha256(...)</c>. Generic and reusable.
    /// </summary>
    private static string VaryKey(ExpressoRuleContext context)
    {
        var headers = AsElement(context.Headers);

        var names = ResolveVaryByNames(context, headers)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var pairs = string.Join("|", names.Select(n => $"{n}={GetStringValue(headers, n)}"));
        var version = GetStringValue(AsElement(context.QueryParameters), VersionQueryParam);
        if (string.IsNullOrEmpty(version))
            version = DefaultVersion;

        return $"v={version}|{pairs}";
    }

    private static IEnumerable<string> ResolveVaryByNames(ExpressoRuleContext context, JsonElement headers)
    {
        // 1) Per-request varyBy from instance data.
        var instanceElement = context.Instance is { } instance ? AsElement(instance.Data) : default;
        var instanceVaryBy = ReadStringArray(instanceElement, VaryByInstanceDataKey);
        if (instanceVaryBy.Count > 0)
            return instanceVaryBy;

        // 2) Domain-supplied config (via MetaData): exact names ∪ prefix matches.
        var meta = AsElement(context.MetaData);
        var configNames = ReadStringArray(meta, VaryByHeadersMetadataKey);
        var configPrefixes = ReadStringArray(meta, VaryByPrefixesMetadataKey);
        if (configNames.Count > 0 || configPrefixes.Count > 0)
        {
            var set = new List<string>(configNames);
            if (configPrefixes.Count > 0 && headers.ValueKind == JsonValueKind.Object)
            {
                foreach (var header in headers.EnumerateObject())
                {
                    if (configPrefixes.Any(p => header.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                        set.Add(header.Name);
                }
            }

            return set;
        }

        // 3) Last resort: every header (safe superset; low hit rate — caller should warn).
        return headers.ValueKind == JsonValueKind.Object
            ? headers.EnumerateObject().Select(p => p.Name)
            : Enumerable.Empty<string>();
    }

    private static JsonElement AsElement(RuleJsonDynamic? value) => value?.AsJsonElement() ?? default;

    private static List<string> ReadStringArray(JsonElement obj, string key)
    {
        var result = new List<string>();
        if (obj.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(obj, key, out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        result.Add(s!);
                }
            }
        }

        return result;
    }

    private static string GetStringValue(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(obj, name, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => value.GetRawText()
            };
        }

        return string.Empty;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;

        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
