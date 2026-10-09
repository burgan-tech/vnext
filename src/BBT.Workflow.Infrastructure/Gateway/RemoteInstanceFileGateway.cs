using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Domain.Shared;
using BBT.Workflow.Files;
using BBT.Workflow.Logging;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Reads an x-storage file that lives in another domain over that domain's internal file endpoint
/// (<c>InstanceController.GetInternalFileAsync</c>). Modeled on <see cref="RemoteRelatedInstanceReader"/> for
/// endpoint resolution, transport usage and error mapping; like it, it sends no caller identity — the read is
/// service-to-service by design.
/// </summary>
/// <remarks>
/// The body is read as raw bytes (never through the JSON string helper) and capped at
/// <see cref="MaxBytes"/>: a larger body (by <c>Content-Length</c> or by its actual length) fails with
/// <c>FileStoreUnavailable(domain)</c>, as does a response without a readable <c>X-File-Handle</c>. The handle's
/// data path is not carried, so <see cref="InstanceFileContent.Path"/> is empty on this path.
/// </remarks>
public sealed class RemoteInstanceFileGateway(
    IRemoteTransport<RemoteInstanceFileGateway> transport,
    IOptions<RemoteOptions> options,
    IDomainDiscoveryResolver endpointResolver) : IInstanceFileGateway
{
    /// <summary>Largest file body accepted from a remote domain (64 MiB, the orchestration sidecar's body limit).</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    /// <inheritdoc />
    public async Task<Result<InstanceFileContent>> ReadAsync(
        string domain, string flow, string instance, string file, CancellationToken cancellationToken)
    {
        var endpointResult = await endpointResolver.GetEndpointAsync(domain, EndpointKind.Url, cancellationToken);
        if (!endpointResult.IsSuccess)
            return Result<InstanceFileContent>.Fail(endpointResult.Error);

        var relativePath = InstanceUrlTemplates.InternalFile(domain, flow, instance, ApiVersionPrefix)
                           + "?file=" + Uri.EscapeDataString(file);

        try
        {
            using var response = await transport.SendAsync(endpointResult.Value!, HttpMethod.Get, relativePath, request =>
            {
                // The client's default Accept is application/json; a file is any type. Identity encoding keeps the
                // body byte-exact on the Dapr wire too (the HTTP shell decompresses on its own).
                request.Headers.Accept.Clear();
                request.Headers.Accept.ParseAdd("*/*");
                request.Headers.AcceptEncoding.Clear();
                request.Headers.AcceptEncoding.ParseAdd("identity");
            }, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await RemoteHttpResponseHelper.MapToErrorAsync(
                    response, cancellationToken, JsonSerializerConstants.JsonOptions);
                return Result<InstanceFileContent>.Fail(error);
            }

            if (response.Content.Headers.ContentLength is > MaxBytes)
                return Result<InstanceFileContent>.Fail(WorkflowErrors.FileStoreUnavailable(domain));

            var handle = ReadHandle(response);
            if (handle is null)
                return Result<InstanceFileContent>.Fail(WorkflowErrors.FileStoreUnavailable(domain));

            var bytes = await ReadBytesAsync(response, cancellationToken);
            if (bytes is null)
                return Result<InstanceFileContent>.Fail(WorkflowErrors.FileStoreUnavailable(domain));

            return Result<InstanceFileContent>.Ok(new InstanceFileContent(handle, string.Empty, bytes, NotModified: false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Same Transient contract as the other remote readers (an HttpClient timeout surfaces as TaskCanceledException).
            return Result<InstanceFileContent>.Fail(Error.Transient("remote_network_error", exception.Message));
        }
    }

    private static FileHandle? ReadHandle(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(HeadersConstants.XFileHandle, out var values))
            return null;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(values.First()));
            return JsonNode.Parse(json) is JsonObject node ? FileHandle.TryRead(node) : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The body bytes, or <c>null</c> when they exceed <see cref="MaxBytes"/>.</summary>
    private static async Task<byte[]?> ReadBytesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var encodings = response.Content.Headers.ContentEncoding;
        if (encodings.Count == 0)
        {
            var raw = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return raw.LongLength > MaxBytes ? null : raw;
        }

        // A proxy that compressed anyway: decompress with the cap applied to the decoded size.
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using Stream decoded = encodings.Contains("gzip") ? new GZipStream(body, CompressionMode.Decompress)
            : encodings.Contains("br") ? new BrotliStream(body, CompressionMode.Decompress)
            : encodings.Contains("deflate") ? new ZLibStream(body, CompressionMode.Decompress)
            : body;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await decoded.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
