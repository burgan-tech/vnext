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

    // vnext-client-sdk-core#99/#142-E: binary raw bodies are written as bytes — in process and after a cache hit.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task ToActionResult_BinaryBody_IsWrittenAsBytes(bool afterCacheRoundTrip)
    {
        var pdf = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x80, 0xFF, 0x00 };
        var output = new FunctionResponseOutput
        {
            Data = pdf,
            IsBinary = true,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/pdf" },
        };
        if (afterCacheRoundTrip)
            output = System.Text.Json.JsonSerializer.Deserialize<FunctionResponseOutput>(System.Text.Json.JsonSerializer.Serialize(output))!;

        var httpContext = new DefaultHttpContext();
        var body = new System.IO.MemoryStream();
        httpContext.Response.Body = body;
        var actionResult = FunctionResponseActionResultMapper.ToActionResult(Result<FunctionResponseOutput>.Ok(output), httpContext);
        await actionResult.ExecuteResultAsync(new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));

        body.ToArray().ShouldBe(pdf);
        httpContext.Response.ContentType.ShouldBe("application/pdf");
        httpContext.Response.ContentLength.ShouldBe(pdf.Length);
    }

    [Fact]
    public async System.Threading.Tasks.Task ToActionResult_BinaryBodyWithoutAType_IsOctetStream()
    {
        var httpContext = new DefaultHttpContext { Response = { Body = new System.IO.MemoryStream() } };
        var actionResult = FunctionResponseActionResultMapper.ToActionResult(
            Result<FunctionResponseOutput>.Ok(new FunctionResponseOutput { Data = new byte[] { 1 }, IsBinary = true }), httpContext);
        await actionResult.ExecuteResultAsync(new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        httpContext.Response.ContentType.ShouldBe("application/octet-stream");
    }
}
