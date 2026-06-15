namespace BBT.Workflow.Validation;

public sealed record SchemaValidationOptions(
    string? Culture = null,
    bool IncludeVocabularyDetails = false,
    bool CustomValidationEnabled = false)
{
    public static SchemaValidationOptions Default { get; } = new();

    public string EffectiveCulture => string.IsNullOrWhiteSpace(Culture) ? "en-US" : Culture.Trim();
}
