using System.Text.Json;

namespace BBT.Workflow.Validation;

public sealed record SchemaValidationProblemDetails(
    string Culture,
    IReadOnlyList<SchemaValidationErrorDetail> Errors);

public sealed record SchemaValidationErrorDetail(
    string Path,
    string Keyword,
    string Code,
    string Message,
    string? Label,
    string? SchemaPath,
    IReadOnlyDictionary<string, JsonElement> Parameters);
