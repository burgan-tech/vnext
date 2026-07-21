using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
        // Deterministic hash helper for building bounded, vary-by-correct cache keys from many/large
        // header sets, e.g. "dcs:" + context.Headers.configKey + ":" + sha256(context.Headers.varyBy).
        interpreter.SetFunction("sha256", (Func<string?, string>)Sha256Hex);
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
}
