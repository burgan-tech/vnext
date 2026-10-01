using System.Text;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions.Schemas;
using TasmanianDevil.Anonymizer.Operators;

namespace BBT.Workflow.Masking;

/// <summary>
/// <see cref="IFieldMaskingEngine"/> over the TasmanianDevil operator layer (package pinned to 0.3.0).
/// <para>
/// Only the deterministic, dependency-free operators are used — <see cref="MaskOperator"/> and
/// <see cref="ReplaceOperator"/>. The analyzer / NER pipeline (<c>PiiEngine</c>,
/// <c>StructuredEngine</c>) is never touched: the schema already names the field, so detection would only add
/// cost and false negatives on the hot read path. <c>EncryptOperator</c> (key used verbatim, fresh nonce per
/// call) is deliberately NOT exposed.
/// </para>
/// <para>
/// <c>x-encryption</c> is not this engine's: <c>hash</c> is applied on write (HMAC under the instance's own salt) and
/// <c>encrypt</c> is resolved by the read filter. Both reach <see cref="Apply"/> only by a wiring mistake and are then
/// served fully masked, never in clear.
/// </para>
/// <para>
/// Lifetime: singleton. The operator instances are stateless for these calls and the SDK's parameter
/// dictionaries are built per call, so concurrent use is safe.
/// </para>
/// </summary>
internal sealed class TasmanianDevilFieldMaskingEngine : IFieldMaskingEngine
{
    private readonly MaskOperator _mask = new();
    private readonly ReplaceOperator _replace = new();

    /// <inheritdoc />
    public string Apply(FieldMaskRule rule, string value)
    {
        try
        {
            return rule.Operator switch
            {
                FieldMaskRule.ReplaceOperator => _replace.Operate(value,
                    new Dictionary<string, object>(1) { [OperatorParams.NewValue] = rule.ReplaceValue ?? string.Empty }),
                // x-encryption is resolved elsewhere (hash on write, encrypt by the read filter); reaching this
                // is a wiring mistake, and it must not turn into a clear value.
                FieldMaskRule.HashOperator or FieldMaskRule.EncryptOperator => FullMask(rule, value),
                _ => Mask(rule, value),
            };
        }
        catch (Exception)
        {
            // Never echo the input (the SDK's messages can quote it) and never fall back to the clear value.
            return FullMask(rule, value);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate(FieldMaskRule rule)
    {
        var errors = new List<string>();
        try
        {
            switch (rule.Operator)
            {
                case FieldMaskRule.ReplaceOperator:
                    if (string.IsNullOrEmpty(rule.ReplaceValue))
                        errors.Add("x-masking operator 'replace' requires a non-empty params.value.");
                    else
                        _replace.Validate(new Dictionary<string, object>(1) { [OperatorParams.NewValue] = rule.ReplaceValue });
                    break;

                case FieldMaskRule.HashOperator:
                case FieldMaskRule.EncryptOperator:
                    // Nothing to check here: the write path owns x-encryption (SchemaComponentValidator gates it).
                    break;

                default:
                    // chars_to_mask is computed per value at apply time; 1 stands in for "some" here.
                    _mask.Validate(MaskParams(rule.MaskingChar, 1));
                    break;
            }
        }
        catch (ArgumentException ex)
        {
            errors.Add(ex.Message);
        }

        return errors;
    }

    /// <summary>
    /// Keeps <see cref="FieldMaskRule.KeepFirst"/> leading and <see cref="FieldMaskRule.KeepLast"/> trailing
    /// characters (counted in Unicode scalar values, so a surrogate pair is never split) and hands the
    /// middle to the SDK's mask operator. A value no longer than the kept characters is returned unchanged —
    /// the documented consequence of a keep window wider than the value.
    /// <para>
    /// The SDK masks per UTF-16 code unit, so a character outside the Basic Multilingual Plane (an emoji)
    /// becomes two masking characters. Length therefore stays observable, as with any length-preserving mask.
    /// </para>
    /// </summary>
    private string Mask(FieldMaskRule rule, string value)
    {
        if (value.Length == 0)
            return value;

        var start = AdvanceRunes(value, 0, rule.KeepFirst);
        var end = RetreatRunes(value, value.Length, rule.KeepLast);
        if (start >= end)
            return value;

        var middle = value[start..end];
        var masked = _mask.Operate(middle, MaskParams(rule.MaskingChar, middle.Length));

        return string.Concat(value.AsSpan(0, start), masked, value.AsSpan(end));
    }

    private static Dictionary<string, object> MaskParams(string maskingChar, int charsToMask) => new(3)
    {
        [OperatorParams.MaskingChar] = string.IsNullOrEmpty(maskingChar) ? FieldMaskRule.DefaultMaskingChar : maskingChar,
        [OperatorParams.CharsToMask] = charsToMask,
        [OperatorParams.FromEnd] = false,
    };

    private static string FullMask(FieldMaskRule rule, string value)
        => new(string.IsNullOrEmpty(rule.MaskingChar) ? '*' : rule.MaskingChar[0], Math.Max(value.Length, 1));

    private static int AdvanceRunes(string value, int index, int count)
    {
        while (count > 0 && index < value.Length)
        {
            index += Rune.TryGetRuneAt(value, index, out var rune) ? rune.Utf16SequenceLength : 1;
            count--;
        }

        return Math.Min(index, value.Length);
    }

    private static int RetreatRunes(string value, int index, int count)
    {
        while (count > 0 && index > 0)
        {
            index -= index >= 2 && char.IsLowSurrogate(value[index - 1]) && char.IsHighSurrogate(value[index - 2]) ? 2 : 1;
            count--;
        }

        return Math.Max(index, 0);
    }
}
