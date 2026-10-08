using BBT.Aether.Results;
using BBT.Workflow.Authorization;

namespace BBT.Workflow.Files;

/// <summary>
/// One x-storage file read of one instance.
/// </summary>
/// <param name="Domain">Domain that owns the instance.</param>
/// <param name="Flow">Flow key of the instance.</param>
/// <param name="Instance">Instance id or key.</param>
/// <param name="File">The handle's <c>file</c> id (compared ordinally).</param>
/// <param name="IfNoneMatch">
/// The request's <c>If-None-Match</c>; when it names the handle's quoted <c>eTag</c> the read answers
/// <see cref="InstanceFileContent.NotModified"/> without touching the store.
/// </param>
/// <param name="Authorization">
/// The public caller's request context. <c>null</c> means a service-to-service read (internal endpoint,
/// <c>ScriptBase.GetFileAsync</c>): neither <c>queryRoles</c> nor <c>x-roles</c> is evaluated.
/// </param>
public sealed record InstanceFileRequest(
    string Domain,
    string Flow,
    string Instance,
    string File,
    string? IfNoneMatch,
    AuthorizationRequestContext? Authorization);

/// <summary>The result of an x-storage file read.</summary>
/// <param name="Handle">The handle as stored in the instance's latest data.</param>
/// <param name="Path">The concrete data path the handle was found at (e.g. <c>files[1]</c>).</param>
/// <param name="Bytes">The file bytes; <c>null</c> when <paramref name="NotModified"/>.</param>
/// <param name="NotModified">The caller's <c>If-None-Match</c> matched; no store read was made.</param>
public sealed record InstanceFileContent(FileHandle Handle, string Path, byte[]? Bytes, bool NotModified);

/// <summary>
/// Reads an x-storage file of ONE instance. The handle is looked up only in that instance's latest data at
/// its schema's x-storage paths, so the binding and object key never come from the caller.
/// </summary>
public interface IInstanceFileAppService
{
    /// <summary>Reads the file; must run inside the instance flow's schema scope.</summary>
    Task<Result<InstanceFileContent>> ReadAsync(InstanceFileRequest request, CancellationToken cancellationToken);
}
