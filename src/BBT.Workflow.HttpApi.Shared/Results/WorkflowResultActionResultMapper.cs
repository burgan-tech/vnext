using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.HttpApi.Results;

public static class WorkflowResultActionResultMapper
{
    private static readonly JsonSerializerOptions DetailJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IActionResult ToActionResult(Result result, HttpContext httpContext)
    {
        if (!result.IsSuccess && TryGetSchemaValidationDetails(result.Error, out var details))
            return CreateSchemaValidationActionResult(result.Error, details, httpContext);

        return result.ToActionResult(httpContext);
    }

    public static IActionResult ToActionResult<T>(Result<T> result, HttpContext httpContext)
    {
        if (!result.IsSuccess && TryGetSchemaValidationDetails(result.Error, out var details))
            return CreateSchemaValidationActionResult(result.Error, details, httpContext);

        return result.ToActionResult(httpContext);
    }

    private static IActionResult CreateSchemaValidationActionResult(
        Error error,
        SchemaValidationProblemDetails details,
        HttpContext httpContext)
    {
        httpContext.Response.Headers["_aether_error_format"] = "true";

        return new ObjectResult(new
        {
            Error = new
            {
                error.Prefix,
                error.Code,
                error.Message,
                Details = (string?)null,
                error.Target,
                ValidationErrors = ToValidationErrorInfo(error.ValidationErrors),
                Data = new
                {
                    Validation = new
                    {
                        details.Errors
                    }
                }
            }
        })
        {
            StatusCode = StatusCodes.Status400BadRequest
        };
    }

    private static IReadOnlyList<object> ToValidationErrorInfo(IEnumerable<ValidationResult>? validationErrors)
    {
        var errors = validationErrors?.ToArray();
        if (errors is null || errors.Length == 0)
            return [];

        return errors
            .Select(error => new
            {
                Message = error.ErrorMessage,
                Members = error.MemberNames.ToArray()
            })
            .Cast<object>()
            .ToArray();
    }

    private static bool TryGetSchemaValidationDetails(
        Error error,
        out SchemaValidationProblemDetails details)
    {
        details = default!;

        if (string.IsNullOrWhiteSpace(error.Detail))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<SchemaValidationProblemDetails>(
                error.Detail,
                DetailJsonOptions);

            if (parsed is null || parsed.Errors.Count == 0)
                return false;

            details = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
