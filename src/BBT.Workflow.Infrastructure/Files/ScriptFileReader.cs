using BBT.Workflow.Gateway;
using BBT.Workflow.Scripting.Functions;

namespace BBT.Workflow.Files;

/// <summary>
/// <see cref="IScriptFileReader"/> over <see cref="IInstanceFileGateway"/>: same domain in process, another domain
/// over its internal file endpoint. A failed read throws, so the script fails like any helper failure.
/// </summary>
public sealed class ScriptFileReader(IInstanceFileGateway gateway) : IScriptFileReader
{
    /// <inheritdoc />
    public async Task<ScriptFile> ReadAsync(
        string domain, string flow, string instance, string file, CancellationToken cancellationToken)
    {
        var result = await gateway.ReadAsync(domain, flow, instance, file, cancellationToken);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"File {file} could not be read: {result.Error.Code} {result.Error.Message}");

        var content = result.Value!;
        return new ScriptFile(
            content.Bytes ?? [], content.Handle.Name, content.Handle.MimeType, content.Handle.Size, content.Handle.ETag);
    }
}
