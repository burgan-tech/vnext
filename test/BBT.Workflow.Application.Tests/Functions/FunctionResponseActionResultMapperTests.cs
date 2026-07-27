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

        var contentResult = actionResult.ShouldBeOfType<ContentResult>();
        // ContentResult preserves the media type verbatim — no "; charset=utf-8" suffix.
        contentResult.ContentType.ShouldBe("application/json");
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

        var contentResult = actionResult.ShouldBeOfType<ContentResult>();
        contentResult.ContentType.ShouldBe("application/xml");
        // string payloads are written verbatim (no JSON quoting) for custom content types.
        contentResult.Content.ShouldBe("<root/>");
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

        actionResult.ShouldBeOfType<ContentResult>().StatusCode.ShouldBe(StatusCodes.Status201Created);
    }

    [Fact]
    public void ToActionResult_WithObjectData_SerializesBodyAsJson()
    {
        var result = Result<FunctionResponseOutput>.Ok(new FunctionResponseOutput
        {
            Data = new { a = 1 }
        });

        var actionResult = FunctionResponseActionResultMapper.ToActionResult(result, new DefaultHttpContext());

        var contentResult = actionResult.ShouldBeOfType<ContentResult>();
        contentResult.Content.ShouldNotBeNull();
        contentResult.Content!.ShouldContain("\"a\"");
        contentResult.Content!.ShouldContain("1");
    }
}
