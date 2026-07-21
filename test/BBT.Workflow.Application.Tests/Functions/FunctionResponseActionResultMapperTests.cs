using System.Collections.Generic;
using BBT.Aether.Results;
using BBT.Workflow.Controllers.Instances;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

public sealed class FunctionResponseActionResultMapperTests
{
    [Fact]
    public void ToActionResult_WithoutContentTypeHeader_DefaultsToApplicationJson()
    {
        var result = Result<FunctionResponseOutput>.Ok(new FunctionResponseOutput
        {
            Data = new { a = 1 }
        });

        var actionResult = FunctionResponseActionResultMapper.ToActionResult(result, new DefaultHttpContext());

        var objectResult = actionResult.ShouldBeOfType<ObjectResult>();
        objectResult.ContentTypes.ShouldContain("application/json");
        objectResult.ContentTypes.ShouldNotContain("application/json; charset=utf-8");
    }

    [Fact]
    public void ToActionResult_WithUserContentType_UsesUserValueCaseInsensitively()
    {
        var result = Result<FunctionResponseOutput>.Ok(new FunctionResponseOutput
        {
            Data = "<root/>",
            Headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/xml"
            }
        });

        var httpContext = new DefaultHttpContext();
        var actionResult = FunctionResponseActionResultMapper.ToActionResult(result, httpContext);

        var objectResult = actionResult.ShouldBeOfType<ObjectResult>();
        objectResult.ContentTypes.ShouldContain("application/xml");
        // content-type must not be duplicated as a raw response header.
        httpContext.Response.Headers.ContainsKey("Content-Type").ShouldBeFalse();
    }

    [Fact]
    public void ToActionResult_WithNonRestrictedHeader_WritesRawHeader()
    {
        var result = Result<FunctionResponseOutput>.Ok(new FunctionResponseOutput
        {
            Data = new { a = 1 },
            Headers = new Dictionary<string, string>
            {
                ["X-Correlation-Id"] = "abc-123"
            }
        });

        var httpContext = new DefaultHttpContext();
        FunctionResponseActionResultMapper.ToActionResult(result, httpContext);

        httpContext.Response.Headers["X-Correlation-Id"].ToString().ShouldBe("abc-123");
    }

    [Fact]
    public void ToActionResult_HonorsStatusCode()
    {
        var result = Result<FunctionResponseOutput>.Ok(new FunctionResponseOutput
        {
            Data = new { a = 1 },
            StatusCode = StatusCodes.Status201Created
        });

        var actionResult = FunctionResponseActionResultMapper.ToActionResult(result, new DefaultHttpContext());

        actionResult.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status201Created);
    }
}
