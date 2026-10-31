using BBT.Aether.Clock;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Files;

/// <summary>One streamed upload to an <c>x-storage</c> field of a flow.</summary>
/// <param name="Domain">Route domain; must be served here.</param>
/// <param name="Flow">Flow key whose master schema declares <paramref name="Path"/>.</param>
/// <param name="Version">Flow version whose schema is read; null resolves like a start does.</param>
/// <param name="Path">The x-storage field path as the schema folds it (<c>passport</c>, <c>files[]</c>, <c>documents[].doc</c>).</param>
/// <param name="Name">Client file name (normalised like inline uploads).</param>
/// <param name="MimeType">The request's <c>Content-Type</c> (normalised like inline uploads).</param>
/// <param name="Body">The request body, read once, never buffered here.</param>
/// <param name="ContentLength">The request's <c>Content-Length</c>; a streamed upload needs one.</param>
public sealed record FileUploadRequest(
    string Domain, string Flow, string? Version, string Path, string? Name, string? MimeType, Stream Body, long? ContentLength);

/// <summary>
/// What an upload answers: the <c>file</c> to reference from a start or transition at <see cref="Path"/>, before
/// <see cref="ExpiresAt"/>. Size and eTag are what the store received.
/// </summary>
public sealed record FileUploadTicket(
    string File, string Component, string Path, string? Name, string? MimeType, long Size, string ETag, DateTimeOffset ExpiresAt);

/// <summary>Streams an upload into the store of an <c>x-storage</c> field and stages it for a start or transition.</summary>
public interface IFileUploadAppService
{
    Task<Result<FileUploadTicket>> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The streamed half of <c>x-storage</c> (<c>transport</c> <c>stream</c> or <c>any</c>): the body goes to the field's
/// store without passing through the instance record or a JSON body, and is staged (<see cref="IFileStagingStore"/>)
/// until a start or transition references its <c>file</c>. The owner is stamped then, by the swap.
/// </summary>
public sealed class FileUploadAppService(
    IComponentCacheStore componentCacheStore,
    IFileOffloadService offloadService,
    IFileBlobStore blobStore,
    IFileStagingStore staging,
    IRuntimeInfoProvider runtimeInfoProvider,
    IClock clock,
    IOptions<FileStorageOptions> options,
    ILogger<FileUploadAppService> logger) : IFileUploadAppService
{
    public async Task<Result<FileUploadTicket>> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        runtimeInfoProvider.Check(request.Domain);

        var flow = await componentCacheStore.GetFlowAsync(request.Domain, request.Flow, request.Version, cancellationToken);
        if (!flow.IsSuccess)
            return Result<FileUploadTicket>.Fail(flow.Error);
        var workflow = flow.Value!;

        var fields = await offloadService.GetFieldsAsync(workflow, cancellationToken);
        if (!fields.IsSuccess)
            return Result<FileUploadTicket>.Fail(fields.Error);

        var field = fields.Value!.FirstOrDefault(f => string.Equals(f.Path, request.Path, StringComparison.Ordinal));
        if (field is null)
            return Invalid(request, "it is not an x-storage path of this flow");
        if (!field.AcceptsStream)
            return Invalid(request, "the field accepts inline content only (x-storage.transport is \"inline\")");
        if (request.ContentLength is not { } length || length < 0)
            return Invalid(request, "a streamed upload needs a Content-Length");
        var limits = options.Value.Upload;
        if (length > limits.MaxBytes)
            return Invalid(request, $"the upload is {length} bytes, the limit is {limits.MaxBytes}");

        var file = Guid.NewGuid().ToString();
        var name = FileOffloadService.NormalizeName(request.Name);
        var mimeType = FileOffloadService.NormalizeMediaType(request.MimeType);
        var stored = await blobStore.PutStreamAsync(field.Binding, file, request.Body, length, mimeType, cancellationToken);
        if (!stored.IsSuccess)
            return Result<FileUploadTicket>.Fail(stored.Error);

        var ttl = TimeSpan.FromMinutes(limits.TicketTtlMinutes);
        var staged = new StagedFile
        {
            Domain = runtimeInfoProvider.Domain,
            Flow = workflow.Key,
            Path = field.Path,
            Component = field.Binding,
            File = file,
            Name = name,
            MimeType = mimeType,
            Size = stored.Value!.Size,
            ETag = stored.Value.Sha256,
            ExpiresAt = new DateTimeOffset(clock.UtcNow, TimeSpan.Zero).Add(ttl),
        };
        await staging.SaveAsync(staged, ttl, cancellationToken);
        logger.FileUploaded(workflow.Key, field.Path, field.Binding, file, staged.Size);

        return Result<FileUploadTicket>.Ok(new FileUploadTicket(
            file, field.Binding, field.Path, name, mimeType, staged.Size, staged.ETag, staged.ExpiresAt));
    }

    private Result<FileUploadTicket> Invalid(FileUploadRequest request, string reason)
    {
        logger.FileUploadRejected(request.Flow, request.Path, reason);
        return Result<FileUploadTicket>.Fail(WorkflowErrors.FileReferenceInvalid(request.Path, reason));
    }
}
