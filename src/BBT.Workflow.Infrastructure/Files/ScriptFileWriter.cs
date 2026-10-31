using BBT.Workflow.Caching;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Functions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Files;

/// <summary>
/// <see cref="IScriptFileWriter"/> over <see cref="IFileBlobStore"/>: a script's output becomes an x-storage object of
/// a flow served here, in a component that flow may use; size and eTag come from the bytes written. A refused or failed
/// write throws, so the script fails like any helper failure.
/// </summary>
public sealed class ScriptFileWriter(
    IFileBlobStore blobStore,
    IComponentCacheStore componentCacheStore,
    IFileOffloadService offloadService,
    IRuntimeInfoProvider runtimeInfoProvider,
    IOptions<FileStorageOptions> options,
    ILogger<ScriptFileWriter> logger) : IScriptFileWriter
{
    /// <inheritdoc />
    public async Task<ScriptStoredFile> WriteAsync(
        string domain, string flow, string instance, string component, Stream content, long length,
        string? name, string? mimeType, CancellationToken cancellationToken)
    {
        if (!runtimeInfoProvider.IsDomainMatch(domain))
            throw new InvalidOperationException($"Files can only be written for a domain served here; '{domain}' is not.");
        var limit = options.Value.Upload.MaxBytes;
        if (length > limit)
            throw new InvalidOperationException($"The file is {length} bytes; the limit is {limit} (FileStorage:Upload:MaxBytes).");

        var workflow = await componentCacheStore.GetFlowAsync(domain, flow, null, cancellationToken);
        if (!workflow.IsSuccess)
            throw new InvalidOperationException($"Flow {domain}/{flow} could not be loaded: {workflow.Error.Code} {workflow.Error.Message}");
        var fields = await offloadService.GetFieldsAsync(workflow.Value!, cancellationToken);
        if (!fields.IsSuccess)
            throw new InvalidOperationException($"The x-storage fields of {domain}/{flow} could not be loaded: {fields.Error.Code}");
        if (!options.Value.AllowedComponents(fields.Value!).Contains(component))
            throw new InvalidOperationException(
                $"Component '{component}' is neither an x-storage binding of {domain}/{flow} nor in FileStorage:AllowedBindings.");

        var file = Guid.NewGuid().ToString();
        var normalizedName = FileOffloadService.NormalizeName(name);
        var normalizedType = FileOffloadService.NormalizeMediaType(mimeType);
        var stored = await blobStore.PutStreamAsync(component, file, content, length, normalizedType, cancellationToken);
        if (!stored.IsSuccess)
            throw new InvalidOperationException($"File could not be stored in {component}: {stored.Error.Code} {stored.Error.Message}");

        logger.FileWrittenByScript(domain, flow, instance, component, file, stored.Value!.Size);
        return new ScriptStoredFile(component, file, normalizedName, normalizedType, stored.Value.Size, stored.Value.Sha256,
            new ScriptFileOwner(domain, flow, instance));
    }

    /// <inheritdoc />
    public string ObjectKey(string file)
    {
        if (!FileHandle.IsFileId(file))
            throw new ArgumentException($"'{file}' is not a file id (a GUID the runtime generated).", nameof(file));
        return options.Value.KeyPrefix + file;
    }
}
