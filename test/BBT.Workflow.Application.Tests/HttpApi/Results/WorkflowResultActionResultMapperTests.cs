using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.HttpApi.Results;
using BBT.Workflow.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BBT.Workflow.HttpApi.Results;

public sealed class WorkflowResultActionResultMapperTests
{
    [Fact]
    public void ToActionResult_WithSchemaValidationDetails_PreservesValidationErrorsAndAddsValidationData()
    {
        // Arrange
        var validationErrors = new List<ValidationResult>
        {
            new("TCKN 11 karakter olmalıdır.", ["customer.identityNumber"])
        };
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"minLength":11}""")!;
        var details = new SchemaValidationProblemDetails(
            "tr-TR",
            [
                new SchemaValidationErrorDetail(
                    Path: "customer.identityNumber",
                    Keyword: "minLength",
                    Code: "schema.minLength",
                    Message: "TCKN 11 karakter olmalıdır.",
                    Label: "TCKN",
                    SchemaPath: "/properties/customer/properties/identityNumber/minLength",
                    Parameters: parameters)
            ]);
        var error = Error.Validation(
                WorkflowErrorCodes.ValidationErrors,
                "JSON schema validation failed",
                validationErrors)
            with
            {
                Detail = JsonSerializer.Serialize(details)
            };

        // Act
        var actionResult = WorkflowResultActionResultMapper.ToActionResult(
            Result.Fail(error),
            new DefaultHttpContext());

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);

        var json = JsonSerializer.Serialize(
            objectResult.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.GetProperty("error");

        Assert.Equal(WorkflowErrorCodes.ValidationErrors, root.GetProperty("code").GetString());
        Assert.Equal("JSON schema validation failed", root.GetProperty("message").GetString());
        Assert.Equal("TCKN 11 karakter olmalıdır.",
            root.GetProperty("validationErrors")[0].GetProperty("message").GetString());

        var validation = root.GetProperty("data").GetProperty("validation");
        Assert.False(validation.TryGetProperty("culture", out _));
        var enrichedError = validation.GetProperty("errors")[0];
        Assert.Equal("customer.identityNumber", enrichedError.GetProperty("path").GetString());
        Assert.Equal("minLength", enrichedError.GetProperty("keyword").GetString());
        Assert.Equal("schema.minLength", enrichedError.GetProperty("code").GetString());
        Assert.Equal("TCKN", enrichedError.GetProperty("label").GetString());
        Assert.Equal(11, enrichedError.GetProperty("parameters").GetProperty("minLength").GetInt32());
    }
}
