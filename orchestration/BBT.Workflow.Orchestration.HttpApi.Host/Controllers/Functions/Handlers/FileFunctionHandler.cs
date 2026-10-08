using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Files;
using BBT.Workflow.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>file</c> system function: <c>functions/file?file=&lt;guid&gt;</c> returns the raw bytes of an
/// x-storage file of THIS instance (latest data only, no subflow descent). Authorization lives in the engine:
/// the state's <c>queryRoles</c> (403) and the path's <c>x-roles</c> (404).
/// </summary>
public sealed class FileFunctionHandler(IInstanceFileAppService fileAppService) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.File;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        var file = request.QueryParameters.GetOrDefault("file");
        if (string.IsNullOrWhiteSpace(file))
        {
            return Result.Fail(WorkflowErrors.FileReferenceInvalid("file", "the 'file' query parameter is required"))
                .ToActionResult(request.HttpContext);
        }

        var result = await fileAppService.ReadAsync(
            new InstanceFileRequest(
                request.Domain,
                request.Workflow,
                request.Instance,
                file,
                request.IfNoneMatch,
                new AuthorizationRequestContext(request.Headers, request.QueryParameters)),
            cancellationToken);
        if (!result.IsSuccess)
            return Result.Fail(result.Error).ToActionResult(request.HttpContext);

        var content = result.Value!;
        var contentType = ContentTypeOf(content.Handle.MimeType);
        var headers = request.HttpContext.Response.Headers;
        headers[HeaderNames.CacheControl] = "private, max-age=31536000, immutable";
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        // The media type is client-declared: the response never runs as a document of this origin (sandboxed, no
        // sub-resources), and an active type (HTML, SVG, XML, script) is only ever downloaded, never rendered inline.
        headers[HeaderNames.ContentSecurityPolicy] = ContentSecurityPolicy;
        var disposition = new ContentDispositionHeaderValue(IsActive(contentType) ? "attachment" : "inline");
        if (!string.IsNullOrWhiteSpace(content.Handle.Name))
            disposition.SetHttpFileName(SafeFileName(content.Handle.Name));
        headers[HeaderNames.ContentDisposition] = disposition.ToString();

        var etag = new EntityTagHeaderValue($"\"{content.Handle.ETag}\"");
        if (content.NotModified)
        {
            headers[HeaderNames.ETag] = etag.ToString();
            return new StatusCodeResult(StatusCodes.Status304NotModified);
        }

        // Compression and Range do not mix (the middleware would gzip the already-sliced 206 body); a
        // pre-set Content-Encoding makes ResponseCompression skip this response (text/plain, svg, json, xml).
        headers[HeaderNames.ContentEncoding] = "identity";

        // FileContentResult handles Range (206/416), If-Range and Accept-Ranges. The binding read is buffered,
        // so a range is an in-memory slice (network saving, not memory saving).
        return new FileContentResult(content.Bytes!, contentType)
        {
            EntityTag = etag,
            EnableRangeProcessing = true,
        };
    }

    internal const string ContentSecurityPolicy = "sandbox; default-src 'none'";
    private const string OctetStream = "application/octet-stream";

    private static readonly HashSet<string> ActiveMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html", "application/xhtml+xml", "image/svg+xml", "text/xml", "application/xml",
        "text/javascript", "application/javascript",
    };

    /// <summary>The stored media type when it parses, else <c>application/octet-stream</c> (never a 500).</summary>
    internal static string ContentTypeOf(string? mimeType)
        => !string.IsNullOrWhiteSpace(mimeType)
           && MediaTypeHeaderValue.TryParse(mimeType, out var parsed)
           && parsed.MediaType.HasValue
           && parsed.MediaType.Value.Contains('/')
            ? parsed.ToString()
            : OctetStream;

    /// <summary>Types a browser would execute or render as a document: always served as an attachment.</summary>
    internal static bool IsActive(string contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) || !parsed.MediaType.HasValue)
            return false;
        var mediaType = parsed.MediaType.Value!;
        return ActiveMediaTypes.Contains(mediaType) || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
    }

    // Control characters (CR/LF included) never reach a header; quotes and non-ASCII are escaped by SetHttpFileName.
    private static string SafeFileName(string name)
        => name.Any(char.IsControl) ? new string(name.Select(c => char.IsControl(c) ? '_' : c).ToArray()) : name;
}
