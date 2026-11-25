using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Evaluators;

/// <summary>
/// Evaluates a Dynamic Expresso expression (a <see cref="ScriptCode"/> with <c>location = "dynamicExpresso"</c>) to a
/// <see cref="string"/> key — CacheAside <c>key</c>, function cache <c>keyExpression</c> / <c>generationKeyExpression</c>,
/// transition <c>resourceLock.keyExpression</c> — without a Roslyn <c>.csx</c> mapping. The expression sees the
/// condition-kind scope (<see cref="ExpressionSignatures.Key"/>): <c>data</c>, <c>input</c>, <c>instance</c>, <c>now</c>,
/// <c>user</c>, <c>headers</c> and the legacy <c>context</c>, plus the <c>sha256</c> and <c>varyKey</c> functions.
/// </summary>
public interface IDynamicExpressoValueEvaluator
{
    /// <summary>
    /// Evaluates the expression to a string. Fails when the script is not a Dynamic Expresso expression, is empty/too
    /// long, cannot be decoded, or throws during evaluation (<c>TaskExecution</c>); fails with
    /// <c>Authorization:110006</c> when it reads the caller's roles and the role provider could not answer.
    /// </summary>
    Task<Result<string>> EvaluateAsync(ScriptCode script, ScriptContext context, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class DynamicExpressoValueEvaluator : IDynamicExpressoValueEvaluator
{
    private readonly ILogger<DynamicExpressoValueEvaluator> logger;
    private readonly IExpressionCompiler compiler;
    private readonly TimeProvider clock;

    /// <summary>Creates the evaluator on the shared process-wide <see cref="ExpressionCompiler"/> and the system clock.</summary>
    public DynamicExpressoValueEvaluator(ILogger<DynamicExpressoValueEvaluator> logger)
        : this(logger, ExpressionCompiler.Shared, TimeProvider.System)
    {
    }

    /// <summary>Creates the evaluator on the given compiler and the system clock.</summary>
    public DynamicExpressoValueEvaluator(ILogger<DynamicExpressoValueEvaluator> logger, IExpressionCompiler compiler)
        : this(logger, compiler, TimeProvider.System)
    {
    }

    /// <summary>Creates the evaluator on the given compiler and clock (the DI constructor).</summary>
    public DynamicExpressoValueEvaluator(
        ILogger<DynamicExpressoValueEvaluator> logger, IExpressionCompiler compiler, TimeProvider clock)
    {
        this.logger = logger;
        this.compiler = compiler;
        this.clock = clock;
    }

    /// <inheritdoc />
    public async Task<Result<string>> EvaluateAsync(
        ScriptCode script, ScriptContext context, CancellationToken cancellationToken = default)
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
            var compiled = compiler.Compile<KeyDelegate>(expression, ExpressionSignatures.Key);
            if (!compiled.IsSuccess)
                throw new InvalidOperationException(compiled.Error.Message);

            // Lazy: only the variables the expression names are materialized (a context-only key builds the legacy
            // rule context and nothing else, and never resolves the caller's roles).
            var used = compiled.Value!.UsedIdentifiers;
            var scope = ExpressionScope.Create(context, clock, context.Caller, ScopeKind.Condition);
            var prepared = await scope.PrepareAsync(used);
            if (!prepared.IsSuccess)
            {
                logger.DynamicExpressoValueEvaluationFailed(prepared.Error.Message ?? prepared.Error.Code);
                return Result<string>.Fail(prepared.Error); // Authorization:110006, never an empty role set
            }

            var a = scope.Bind(ExpressionSignatures.Key.Parameters, used);
            var value = compiled.Value.Invoke(
                (RuleJsonDynamic?)a[0], (RuleJsonDynamic?)a[1], (ExpressionInstance?)a[2], (DateTimeOffset)a[3]!,
                (ExpressionUser?)a[4], (ExpressoRuleContext?)a[5], (ExpressionHeaders?)a[6]);
            return Result<string>.Ok(value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var message = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
            logger.DynamicExpressoValueEvaluationFailed(message);
            return Result<string>.Fail(Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"Dynamic Expresso evaluation failed: {message}"));
        }
    }

    /// <summary>
    /// Lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="input"/> (empty string for null).
    /// </summary>
    internal static string Sha256Hex(string? input)
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
    internal static string VaryKey(ExpressoRuleContext context)
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
