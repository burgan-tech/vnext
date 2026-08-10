using System.ComponentModel.DataAnnotations;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Shared publish-time validation for every <see cref="ScriptCode"/> slot a component definition can
/// carry (mappings, rules, timers, key expressions, output handlers).
/// <para>
/// The slot exists because a script body was authored somewhere; the body itself is inlined into
/// <c>code</c> by the domain build step. A slot that survives publish with only a <c>location</c> is
/// therefore always a build accident, and nothing downstream reports it: the converter defaults the
/// encoding to Base64 (<see cref="ScriptCodeJsonConverter"/>), <c>Convert.FromBase64String("")</c>
/// succeeds, and <see cref="ScriptCode.HasMappingCode"/> turns false — so every executor silently skips
/// the mapping, while a rule blows up mid-transition when Roslyn compiles an empty script. This
/// validator is the only place that gap is visible, so it must run for every slot.
/// </para>
/// <para>
/// Rules mirror the <c>vnext-schema</c> guard: a <see cref="MappingType.Global"/> slot carries no body
/// and is always accepted; anything else must resolve to a non-empty body, either inline
/// (Native/Base64) or through a <c>sys-mappings</c> reference (<see cref="CodeEncoding.Reference"/>).
/// </para>
/// </summary>
public static class ScriptCodeValidator
{
    /// <summary>
    /// Validates a single script slot, appending one error per problem found.
    /// </summary>
    /// <param name="script">The slot to validate. A null slot is valid — optionality is the holder's concern.</param>
    /// <param name="member">
    /// Dotted path of the slot for the error's member name (e.g.
    /// <c>Workflow.States[approval].OnEntries[0].Mapping</c>).
    /// </param>
    /// <param name="errors">Error sink; both validation result types expose their list directly.</param>
    public static void Validate(ScriptCode? script, string member, IList<ValidationResult> errors)
    {
        if (script is null)
        {
            return;
        }

        // Global declares that the body lives outside this slot: HasMappingCode is false and DecodedCode
        // returns empty by design, so there is nothing to require here whatever the encoding says.
        if (script.Type.Equals(MappingType.Global))
        {
            return;
        }

        if (script.Encoding.Equals(CodeEncoding.Reference))
        {
            ValidateReference(script, member, errors);
            return;
        }

        if (string.IsNullOrWhiteSpace(script.Code))
        {
            errors.Add(Error(
                $"Script '{member}' declares only 'location' ('{script.Location}') and no 'code', so it is " +
                "never executed. The script body must be inlined into 'code' by the domain build step. " +
                $"Use type '{MappingType.Global.Code}' if the slot is intentionally empty.",
                member));
            return;
        }

        string decoded;
        try
        {
            decoded = script.DecodedCode;
        }
        catch (InvalidOperationException)
        {
            errors.Add(Error(
                $"Script '{member}' ('{script.Location}') declares '{script.Encoding.Code}' encoding but its " +
                "'code' is not valid Base64.",
                member));
            return;
        }

        if (string.IsNullOrWhiteSpace(decoded))
        {
            errors.Add(Error(
                $"Script '{member}' ('{script.Location}') decodes to an empty body, so it is never executed.",
                member));
        }
    }

    /// <summary>
    /// Validates a <c>REF</c> slot. The body is resolved from the <c>sys-mappings</c> component store at
    /// compile time by key/domain/version, so an incomplete reference is an unresolvable mapping that only
    /// surfaces mid-transition.
    /// </summary>
    private static void ValidateReference(ScriptCode script, string member, IList<ValidationResult> errors)
    {
        var reference = script.CodeReference;
        if (reference is null)
        {
            errors.Add(Error(
                $"Script '{member}' ('{script.Location}') declares '{CodeEncoding.Reference.Code}' encoding but " +
                $"'code' is not a reference object. Provide a {RuntimeSysSchemaInfo.Mappings} reference " +
                "(key/domain/flow/version) or switch the encoding to an inline one.",
                member));
            return;
        }

        var missing = new List<string>(3);
        if (string.IsNullOrWhiteSpace(reference.Key))
        {
            missing.Add("key");
        }

        if (string.IsNullOrWhiteSpace(reference.Domain))
        {
            missing.Add("domain");
        }

        if (string.IsNullOrWhiteSpace(reference.Version))
        {
            missing.Add("version");
        }

        if (missing.Count > 0)
        {
            errors.Add(Error(
                $"Script '{member}' reference is incomplete; missing {string.Join(", ", missing)}.",
                member));
        }

        // An empty flow is tolerated (the store is always read under the sys-mappings schema), but a
        // populated one pointing elsewhere means the author referenced the wrong component type.
        if (!string.IsNullOrWhiteSpace(reference.Flow) &&
            !string.Equals(reference.Flow, RuntimeSysSchemaInfo.Mappings, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(Error(
                $"Script '{member}' reference must point at the '{RuntimeSysSchemaInfo.Mappings}' flow but " +
                $"points at '{reference.Flow}'.",
                member));
        }
    }

    private static ValidationResult Error(string message, string member) => new(message, [member]);
}
