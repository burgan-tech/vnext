using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Orchestration.Controllers.Instances;

public sealed class InstanceResponseActionResultMapperTests
{
    private static Result<StartInstanceOutput> SuccessResult() =>
        Result<StartInstanceOutput>.Ok(new StartInstanceOutput
        {
            Id = Guid.NewGuid(),
            Key = "instance-1",
            Status = InstanceStatus.Busy
        });

    [Fact]
    public void ToActionResult_AsyncSuccess_Returns202Accepted()
    {
        var actionResult = InstanceResponseActionResultMapper.ToActionResult(
            SuccessResult(), new DefaultHttpContext(), async: true);

        actionResult.ShouldBeAssignableTo<ObjectResult>()!.StatusCode.ShouldBe(StatusCodes.Status202Accepted);
    }

    [Fact]
    public void ToActionResult_SyncSuccess_KeepsOkStatus()
    {
        var actionResult = InstanceResponseActionResultMapper.ToActionResult(
            SuccessResult(), new DefaultHttpContext(), async: false);

        var objectResult = actionResult.ShouldBeAssignableTo<ObjectResult>()!;
        (objectResult.StatusCode is null or StatusCodes.Status200OK).ShouldBeTrue();
    }

    [Fact]
    public void ToActionResult_AsyncFailure_PreservesErrorStatus()
    {
        // Schema-validation failures are mapped to a 400 ObjectResult by
        // WorkflowResultActionResultMapper itself (no Aether/DI problem-result path),
        // which makes this a DI-free way to assert the async flag never touches errors.
        var actionResult = InstanceResponseActionResultMapper.ToActionResult(
            SchemaValidationFailure(), new DefaultHttpContext(), async: true);

        var objectResult = actionResult.ShouldBeAssignableTo<ObjectResult>()!;
        objectResult.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void ToActionResult_AsyncCustomOutputResponse_UsesScriptStatusNot202()
    {
        var result = Result<StartInstanceOutput>.Ok(new StartInstanceOutput
        {
            Id = Guid.NewGuid(),
            HasOutputResponse = true,
            OutputData = new { ok = true },
            OutputStatusCode = StatusCodes.Status201Created
        });

        var actionResult = InstanceResponseActionResultMapper.ToActionResult(
            result, new DefaultHttpContext(), async: true);

        actionResult.ShouldBeAssignableTo<ObjectResult>()!.StatusCode.ShouldBe(StatusCodes.Status201Created);
    }

    private static Result<StartInstanceOutput> SchemaValidationFailure()
    {
        var details = new SchemaValidationProblemDetails(
            "en-US",
            [
                new SchemaValidationErrorDetail(
                    Path: "customer.identityNumber",
                    Keyword: "minLength",
                    Code: "schema.minLength",
                    Message: "identityNumber too short.",
                    Label: "Identity Number",
                    SchemaPath: "/properties/customer/properties/identityNumber/minLength",
                    Parameters: JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"minLength":11}""")!)
            ]);

        var error = Error.Validation(
                WorkflowErrorCodes.ValidationErrors,
                "JSON schema validation failed",
                new List<ValidationResult> { new("identityNumber too short.", ["customer.identityNumber"]) })
            with
            {
                Detail = JsonSerializer.Serialize(details)
            };

        return Result<StartInstanceOutput>.Fail(error);
    }
}
