using System.Text.Json;
using BBT.Workflow.Validation;

namespace BBT.Workflow.Agents;

/// <summary>
/// Checks model-produced JSON — tool arguments and the structured output — against its JSON schema
/// (issue #1105). The model is untrusted input: a tool never runs, and an output never reaches instance
/// data, unless it matches. The answer is a short error list worded for the model, so it can correct itself.
/// </summary>
public static class AgentSchemaCheck
{
    private const int MaxErrors = 10;
    private const int MaxLength = 800;

    /// <summary>Null when <paramref name="data"/> matches <paramref name="schema"/>; otherwise the errors, one per field.</summary>
    public static string? Describe(IJsonSchemaValidator validator, JsonElement schema, JsonElement data)
    {
        var result = validator.Validate(schema, data);
        if (result.IsSuccess)
            return null;

        var errors = (result.Error.ValidationErrors ?? [])
            .Select(e =>
            {
                var member = e.MemberNames.FirstOrDefault(m => !string.IsNullOrEmpty(m));
                return member is null ? e.ErrorMessage : $"{member}: {e.ErrorMessage}";
            })
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxErrors)
            .ToList();
        var text = errors.Count > 0 ? string.Join("; ", errors) : result.Error.Message ?? "does not match the schema";
        return text.Length > MaxLength ? text[..MaxLength] + "…" : text;
    }
}
