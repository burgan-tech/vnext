using System;
using System.Collections.Generic;
using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Deterministic stand-in for the Infrastructure adapter (which is internal and SDK-backed and is pinned
/// separately by <c>TasmanianDevilFieldMaskingEngineTests</c>). Same observable semantics for ASCII input:
/// keep <c>KeepFirst</c>/<c>KeepLast</c>, mask the middle; <c>replace</c> writes the value.
/// </summary>
internal sealed class FakeFieldMaskingEngine : IFieldMaskingEngine
{
    public int Calls { get; private set; }

    public string Apply(FieldMaskRule rule, string value)
    {
        Calls++;
        if (rule.Operator == FieldMaskRule.ReplaceOperator)
            return rule.ReplaceValue ?? string.Empty;
        if (rule.Operator is FieldMaskRule.HashOperator or FieldMaskRule.EncryptOperator)
            return "********";
        if (rule.KeepFirst + rule.KeepLast >= value.Length)
            return value;
        var middle = value.Length - rule.KeepFirst - rule.KeepLast;
        return value[..rule.KeepFirst] + new string(rule.MaskingChar[0], middle) + value[(value.Length - rule.KeepLast)..];
    }

    public IReadOnlyList<string> Validate(FieldMaskRule rule) => Array.Empty<string>();
}
