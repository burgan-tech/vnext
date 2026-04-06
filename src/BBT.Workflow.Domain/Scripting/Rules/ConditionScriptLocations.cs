namespace BBT.Workflow.Scripting.Rules;

/// <summary>
/// Identifies condition script storage / evaluation kinds via <see cref="Definitions.ScriptCode.Location"/>.
/// </summary>
public static class ConditionScriptLocations
{
    /// <summary>
    /// Plain-text boolean expression evaluated with Dynamic Expresso against <see cref="ExpressoRuleContext"/>.
    /// </summary>
    public const string DynamicExpresso = "dynamicExpresso";

    /// <summary>
    /// Maximum allowed length for a Dynamic Expresso condition string (after decoding).
    /// </summary>
    public const int MaxDynamicExpressoExpressionLength = 8000;

    /// <summary>
    /// Returns whether the script location selects Dynamic Expresso evaluation.
    /// </summary>
    public static bool IsDynamicExpresso(string? location) =>
        string.Equals(location, DynamicExpresso, StringComparison.OrdinalIgnoreCase);
}
