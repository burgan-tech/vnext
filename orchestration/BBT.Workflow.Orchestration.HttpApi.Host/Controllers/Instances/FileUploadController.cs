using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Files;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Orchestration.Controllers.Instances;

/// <summary>
/// Streamed uploads for <c>x-storage</c> fields whose <c>transport</c> is <c>stream</c> or <c>any</c>
/// (the alternative to base64 <c>content</c> in a JSON body).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class FileUploadController(
    IFileUploadAppService uploadAppService,
    IOptions<FileStorageOptions> options) : AetherControllerBase
{
    /// <summary>
    /// Stores the raw request body in the store of the flow's <c>x-storage</c> field at <paramref name="path"/>
    /// and answers a ticket. Reference the ticket's <c>file</c> at the same path in a start or transition body
    /// (<c>{ "file": "&lt;guid&gt;" }</c>, other members of the node are kept) before <c>expiresAt</c>; the swap stamps
    /// the instance as owner. The body is read once into a buffer of <c>Content-Length</c> bytes and stored as one
    /// Dapr binding message (no JSON, no base64). The file's media type is the request's <c>Content-Type</c>.
    /// </summary>
    /// <param name="domain">Domain served by this runtime.</param>
    /// <param name="workflow">Flow key whose master schema declares the field.</param>
    /// <param name="path">The field path as the schema declares it: <c>passport</c>, <c>files[]</c>, <c>documents[].doc</c>.</param>
    /// <param name="name">The file name to record in the handle.</param>
    /// <param name="version">Flow version whose master schema is read (query, as on start); omitted resolves like a start.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <response code="201">Stored and staged; the body is the ticket.</response>
    /// <response code="400">Unknown path, a field that accepts inline content only, or no <c>Content-Length</c>.</response>
    /// <response code="413">The body is larger than <c>FileStorage:Upload:MaxBytes</c>; nothing was stored.</response>
    /// <response code="503">The store failed; nothing was staged, the upload can be retried.</response>
    [HttpPost("{domain}/workflows/{workflow}/files")]
    [ProducesResponseType(typeof(FileUploadTicket), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UploadAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromQuery] string path,
        [FromQuery] string? name = null,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var maxBytes = options.Value.Upload.MaxBytes;
        if (Request.ContentLength > maxBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Payload Too Large",
                Detail = $"The upload is {Request.ContentLength} bytes; the limit is {maxBytes} (FileStorage:Upload:MaxBytes).",
            });
        }

        // The host-wide Kestrel limit is sized for JSON bodies; this route's limit is the upload limit.
        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maxBytes;

        var result = await uploadAppService.UploadAsync(
            new FileUploadRequest(domain, workflow, version, path, name, Request.ContentType, Request.Body, Request.ContentLength),
            cancellationToken);
        return FromResult(result, _ => StatusCodes.Status201Created);
    }
}
