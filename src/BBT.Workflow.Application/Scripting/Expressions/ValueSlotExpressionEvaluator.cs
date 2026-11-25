using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting.Rules;
using Microsoft.Extensions.Logging;
using DecisionFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Rules.RuleJsonDynamic,
    BBT.Workflow.Scripting.Expressions.ExpressionInstance, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser, BBT.Workflow.Scripting.Rules.ExpressoRuleContext,
    BBT.Workflow.Scripting.Expressions.ExpressionHeaders,
    BBT.Workflow.Scripting.Expressions.ExpressionAgentOutcome, BBT.Workflow.Scripting.Expressions.ExpressionAgentCandidates,
    object>;
using ValueFn = System.Func<
    BBT.Workflow.Scripting.Rules.RuleJsonDynamic, BBT.Workflow.Scripting.Rules.RuleJsonDynamic,
    BBT.Workflow.Scripting.Expressions.ExpressionInstance, System.DateTimeOffset,
    BBT.Workflow.Scripting.Expressions.ExpressionUser, BBT.Workflow.Scripting.Rules.ExpressoRuleContext,
    BBT.Workflow.Scripting.Expressions.ExpressionHeaders, object>;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>The value slots outside task mappings that honour <c>location: "dynamicExpresso"</c>.</summary>
public enum ValueSlotKind
{
    /// <summary>Transition <c>timer</c>, workflow <c>attributes.timeout.mapping</c>, SubFlow timeout override → <see cref="TimerSchedule"/>.</summary>
    Timer,

    /// <summary>Agent state <c>prompt</c> → <see cref="string"/>.</summary>
    AgentPrompt,

    /// <summary>Agent <c>decision.mapping</c> (mode <c>script</c>) → transition key or null.</summary>
    AgentDecision
}

/// <summary>
/// Evaluates a <see cref="ScriptCode"/> labelled <c>location: "dynamicExpresso"</c> in a value slot (timer, timeout,
/// timeout override, agent prompt, agent decision) as a Dynamic Expresso expression over the condition-kind scope
/// (<c>data</c>, <c>input</c>, <c>instance</c>, <c>now</c>, <c>user</c>, <c>context</c>, <c>headers</c>; the decision adds
/// <c>outcome</c> and <c>candidates</c>), and converts its value to the slot's result type. Singleton, stateless; every
/// expression goes through the shared <see cref="IExpressionCompiler"/>. Never throws: failures are results.
/// </summary>
/// <remarks>
/// Compatibility (no breaking change): before the label was honoured such a script ran as C#. Callers run the slot
/// through <see cref="WithScriptFallbackAsync{T}"/>, which falls back to the C# path when the expression fails and
/// warns (<c>ScriptLabelledDynamicExpressoRanAsScript</c>); when both fail the expression's error is reported.
/// </remarks>
public sealed class ValueSlotExpressionEvaluator(IExpressionCompiler compiler, TimeProvider clock)
{
    /// <summary>Error code of an expression that compiled but failed to evaluate or returned an unusable value.</summary>
    public const string EvaluationFailedCode = "Expression.ValueSlotFailed";

    /// <summary>Process-wide instance over <see cref="ExpressionCompiler.Shared"/> and the system clock.</summary>
    public static ValueSlotExpressionEvaluator Shared { get; } = new(ExpressionCompiler.Shared, TimeProvider.System);

    /// <summary>
    /// Compiles <paramref name="script"/> as a <paramref name="kind"/> expression without running it (publish-time
    /// check). Returns null when it compiles, else the reason.
    /// </summary>
    public static string? CompileError(IExpressionCompiler compiler, ScriptCode script, ValueSlotKind kind)
    {
        var text = Decode(script, out var decodeError);
        if (text is null)
            return decodeError;

        var compiled = kind == ValueSlotKind.AgentDecision
            ? compiler.Compile<DecisionFn>(text, ExpressionSignatures.AgentDecision) is { IsSuccess: false } f1 ? f1.Error.Message : null
            : compiler.Compile<ValueFn>(text, ExpressionSignatures.ValueSlot) is { IsSuccess: false } f2 ? f2.Error.Message : null;
        return compiled;
    }

    /// <summary>Evaluates a timer / timeout expression and converts its value (<see cref="ToTimerSchedule"/>).</summary>
    public async Task<Result<TimerSchedule>> EvaluateTimerAsync(ScriptCode script, ScriptContext context)
    {
        var value = await EvaluateValueAsync(script, context, "timer");
        return value.IsSuccess ? ToTimerSchedule(value.Value) : Result<TimerSchedule>.Fail(value.Error);
    }

    /// <summary>Evaluates an agent prompt expression; the value must be text.</summary>
    public async Task<Result<string>> EvaluatePromptAsync(ScriptCode script, ScriptContext context)
    {
        var value = await EvaluateValueAsync(script, context, "agent prompt");
        if (!value.IsSuccess)
            return Result<string>.Fail(value.Error);

        return value.Value switch
        {
            null => Fail<string>("agent prompt", "the expression returned null; a prompt expression must return text."),
            string text => Result<string>.Ok(text),
            JsonElement element => Result<string>.Ok(element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText()),
            var other => Result<string>.Ok(Convert.ToString(other, CultureInfo.InvariantCulture) ?? string.Empty)
        };
    }

    /// <summary>
    /// Evaluates an agent decision expression with <c>outcome</c> and <c>candidates</c>; the value is a transition key
    /// (text) or null (no decision).
    /// </summary>
    public async Task<Result<string?>> EvaluateDecisionAsync(
        ScriptCode script, ScriptContext context, AgentRunOutcome outcome, IReadOnlyList<AgentCandidate> candidates)
    {
        const string slot = "agent decision";
        var text = Decode(script, out var decodeError);
        if (text is null)
            return Fail<string?>(slot, decodeError!);

        var compiled = compiler.Compile<DecisionFn>(text, ExpressionSignatures.AgentDecision);
        if (!compiled.IsSuccess)
            return Fail<string?>(slot, $"the expression does not compile: {compiled.Error.Message}");

        var scope = ExpressionScope.Create(context, clock, context.Caller, ScopeKind.Condition);
        var used = compiled.Value!.UsedIdentifiers;
        var prepared = await scope.PrepareAsync(used);
        if (!prepared.IsSuccess)
            return Result<string?>.Fail(prepared.Error);

        object? value;
        try
        {
            var a = scope.Bind(ExpressionSignatures.ValueSlot.Parameters, used);
            value = Normalize(compiled.Value.Invoke(
                (RuleJsonDynamic?)a[0]!, (RuleJsonDynamic?)a[1]!, (ExpressionInstance?)a[2]!, (DateTimeOffset)a[3]!,
                (ExpressionUser?)a[4]!, (ExpressoRuleContext?)a[5]!, (ExpressionHeaders?)a[6]!,
                new ExpressionAgentOutcome(outcome), new ExpressionAgentCandidates(candidates)));
        }
        catch (Exception ex)
        {
            return Fail<string?>(slot, Unwrap(ex).Message);
        }

        return value switch
        {
            null => Result<string?>.Ok(null),
            string key => Result<string?>.Ok(key),
            _ => Fail<string?>(slot, $"the expression returned a {Describe(value)}; a decision expression must return a transition key (text) or null.")
        };
    }

    /// <summary>
    /// Runs a value slot labelled <c>dynamicExpresso</c>: the expression first; when it fails, the slot's C# path
    /// (<paramref name="script"/>, its pre-label meaning) — a success there is returned with one
    /// <c>ScriptLabelledDynamicExpressoRanAsScript</c> warning; when both fail, the expression's error. A caller-roles
    /// provider failure (<c>Authorization:110006</c>, retryable) is returned as is, never retried as C#. Cancellation of
    /// <paramref name="cancellationToken"/> propagates.
    /// </summary>
    public static async Task<Result<T>> WithScriptFallbackAsync<T>(
        Func<Task<Result<T>>> expression,
        Func<Task<T>> script,
        string slot,
        ILogger? logger,
        CancellationToken cancellationToken = default)
    {
        var evaluated = await expression();
        if (evaluated.IsSuccess || evaluated.Error.Code == WorkflowErrors.CallerRolesUnavailable().Code)
            return evaluated;

        T value;
        try
        {
            value = await script();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return evaluated; // what the author wrote is the expression: report its error, not Roslyn's
        }

        logger?.ScriptLabelledDynamicExpressoRanAsScript(slot, evaluated.Error.Message ?? evaluated.Error.Code);
        return Result<T>.Ok(value);
    }

    /// <summary>
    /// Converts a timer expression's value (decision D-2): <see cref="TimeSpan"/> → duration;
    /// <see cref="DateTime"/> / <see cref="DateTimeOffset"/> → absolute instant; text (or a JSON string) → an ISO-8601
    /// duration (<c>PT15M</c>) or else an ISO-8601 date-time (invariant culture, UTC when no offset); null → immediate.
    /// Anything else — numbers included, their unit is ambiguous — fails.
    /// </summary>
    public static Result<TimerSchedule> ToTimerSchedule(object? value)
    {
        const string slot = "timer";
        switch (value)
        {
            case null:
                return Result<TimerSchedule>.Ok(TimerSchedule.Immediate());
            case TimeSpan duration:
                return Result<TimerSchedule>.Ok(TimerSchedule.FromDuration(duration));
            case DateTimeOffset instant:
                return Result<TimerSchedule>.Ok(TimerSchedule.FromDateTime(instant.UtcDateTime));
            case DateTime dateTime:
                return Result<TimerSchedule>.Ok(TimerSchedule.FromDateTime(dateTime));
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return FromText(element.GetString()!);
            case string text:
                return FromText(text);
            default:
                return Fail<TimerSchedule>(slot,
                    $"the expression returned a {Describe(value)}; a timer expression must return a TimeSpan, a DateTime / " +
                    "DateTimeOffset, an ISO-8601 duration (\"PT15M\") or date-time string, or null (immediate). Numbers are " +
                    "rejected because their unit is ambiguous: use TimeSpan.FromSeconds(...) or TimeSpan.FromMinutes(...).");
        }

        static Result<TimerSchedule> FromText(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
                return Fail<TimerSchedule>(slot, "the expression returned an empty string; expected an ISO-8601 duration or date-time.");

            if (trimmed[0] is 'P' or 'p' || trimmed.StartsWith("-P", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Result<TimerSchedule>.Ok(TimerSchedule.FromDuration(System.Xml.XmlConvert.ToTimeSpan(trimmed)));
                }
                catch (FormatException)
                {
                    // not a duration: try a date-time below
                }
            }

            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return Result<TimerSchedule>.Ok(TimerSchedule.FromDateTime(parsed.UtcDateTime));

            return Fail<TimerSchedule>(slot,
                $"the expression returned \"{trimmed}\", which is neither an ISO-8601 duration (\"PT15M\") nor an ISO-8601 date-time.");
        }
    }

    private async Task<Result<object?>> EvaluateValueAsync(ScriptCode script, ScriptContext context, string slot)
    {
        var text = Decode(script, out var decodeError);
        if (text is null)
            return Fail<object?>(slot, decodeError!);

        var compiled = compiler.Compile<ValueFn>(text, ExpressionSignatures.ValueSlot);
        if (!compiled.IsSuccess)
            return Fail<object?>(slot, $"the expression does not compile: {compiled.Error.Message}");

        var scope = ExpressionScope.Create(context, clock, context.Caller, ScopeKind.Condition);
        var used = compiled.Value!.UsedIdentifiers;
        var prepared = await scope.PrepareAsync(used);
        if (!prepared.IsSuccess)
            return Result<object?>.Fail(prepared.Error);

        try
        {
            var a = scope.Bind(ExpressionSignatures.ValueSlot.Parameters, used);
            return Result<object?>.Ok(Normalize(compiled.Value.Invoke(
                (RuleJsonDynamic?)a[0]!, (RuleJsonDynamic?)a[1]!, (ExpressionInstance?)a[2]!, (DateTimeOffset)a[3]!,
                (ExpressionUser?)a[4]!, (ExpressoRuleContext?)a[5]!, (ExpressionHeaders?)a[6]!)));
        }
        catch (Exception ex)
        {
            return Fail<object?>(slot, Unwrap(ex).Message);
        }
    }

    /// <summary>
    /// A borrowed JSON view the expression returned becomes an owned value: a JSON string its text, JSON null or missing
    /// null, anything else an independent <see cref="JsonElement"/> copy.
    /// </summary>
    private static object? Normalize(object? value)
    {
        var element = value switch
        {
            RuleJsonDynamic view => view.AsJsonElement(),
            JsonElement e => e,
            _ => (JsonElement?)null
        };
        if (element is null)
            return value;

        return element.Value.ValueKind switch
        {
            JsonValueKind.String => element.Value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.Value.Clone()
        };
    }

    private static string? Decode(ScriptCode script, out string? error)
    {
        error = null;
        string text;
        try
        {
            text = script.DecodedCode.Trim();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            error = $"the expression could not be decoded: {ex.Message}";
            return null;
        }

        if (text.Length == 0)
        {
            error = "the expression is empty.";
            return null;
        }

        if (text.Length > ConditionScriptLocations.MaxDynamicExpressoExpressionLength)
        {
            error = $"the expression exceeds the maximum length ({ConditionScriptLocations.MaxDynamicExpressoExpressionLength}).";
            return null;
        }

        return text;
    }

    private static string Describe(object value) => value switch
    {
        JsonElement element => $"JSON {element.ValueKind.ToString().ToLowerInvariant()}",
        _ => value.GetType().Name
    };

    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    private static Result<T> Fail<T>(string slot, string detail) =>
        Result<T>.Fail(Error.Failure(EvaluationFailedCode, $"Dynamic Expresso {slot} expression failed: {detail}"));
}
