using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
using BBT.Workflow.Validation;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Functions.Validation;

/// <summary>
/// Validates function request bodies against the function's declared input schema.
/// Mirrors the transition-level schema gate (<c>TransitionValidationService</c>) and reuses the same
/// collaborators, so functions and transitions report schema violations identically.
/// </summary>
public sealed class FunctionRequestValidationService(
    IJsonSchemaValidator schemaValidator,
    IComponentCacheStore componentCacheStore,
    ILogger<FunctionRequestValidationService> logger) : IFunctionRequestValidationService
{
    /// <inheritdoc />
    public async Task<Result> ValidateRequestAsync(
        Function function,
        JsonElement? body,
        IReadOnlyDictionary<string, string?>? headers = null,
        CancellationToken cancellationToken = default)
    {
        // Guard: no declared input contract - behave exactly as before contract declaration existed.
        if (function.InputSchema is null)
            return Result.Ok();

        // Guard: no body to validate. Bodyless verbs (GET) and empty payloads are not the schema's concern;
        // a function that must receive a body should declare it required in the schema of a body-carrying verb.
        if (!body.HasValue || body.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return Result.Ok();

        var schemaResult = await componentCacheStore.GetSchemaAsync(function.InputSchema, cancellationToken);
        if (!schemaResult.IsSuccess)
            return Result.Fail(schemaResult.Error);

        var validationResult = schemaValidator.Validate(
            schemaResult.Value!.Schema,
            body,
            CreateSchemaValidationOptions(headers));

        if (!validationResult.IsSuccess)
            logger.FunctionInputSchemaValidationFailed(function.Key, function.InputSchema.Key);

        return validationResult;
    }

    private static SchemaValidationOptions CreateSchemaValidationOptions(IReadOnlyDictionary<string, string?>? headers)
    {
        return new SchemaValidationOptions(
            Culture: LanguageResolver.ResolveCulture(headers),
            IncludeVocabularyDetails: true,
            CustomValidationEnabled: true);
    }
}
