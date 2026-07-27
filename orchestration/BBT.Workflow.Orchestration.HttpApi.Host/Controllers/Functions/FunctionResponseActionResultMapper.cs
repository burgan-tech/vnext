using System.Text.Json;
using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Functions;
using BBT.Workflow.Orchestration.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

internal static class FunctionResponseActionResultMapper
{
    public static IActionResult ToActionResult(
        Result<FunctionResponseOutput> result,
        HttpContext httpContext)
    {
        if (!result.IsSuccess)
            return result.ToActionResult(httpContext);

        var output = result.Value!;
        ResponseOutputWriter.ApplyHeaders(output.Headers, httpContext);

        // Use ContentResult (not ObjectResult) so the author-supplied media type is written
        // verbatim. ObjectResult runs content negotiation and the SystemTextJson output formatter
        // appends "; charset=utf-8" to the resolved media type; ContentResult preserves the exact
        // content-type string when it carries no charset of its own.
        return new ContentResult
        {
            StatusCode = output.StatusCode ?? StatusCodes.Status200OK,
            ContentType = ResponseOutputWriter.ResolveContentType(output.Headers),
            Content = SerializeContent(output.Data)
        };
    }

    /// <summary>
    /// Serializes the response payload. String payloads are written verbatim (the author has
    /// already produced the wire representation, e.g. XML for a custom Content-Type); any other
    /// object is serialized with the centralized JSON options for parity with the previous
    /// ObjectResult behavior.
    /// </summary>
    private static string? SerializeContent(object? data) => data switch
    {
        null => null,
        string s => s,
        _ => JsonSerializer.Serialize(data, JsonSerializerConstants.JsonOptions)
    };
}
