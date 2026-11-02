using System.IO;

namespace BBT.Workflow.Scripting.Functions;

/// <summary>
/// An x-storage file read by a script through <c>ScriptBase.GetFileAsync</c>. <see cref="Content"/> holds the bytes,
/// verified against the handle; <see cref="OpenRead"/> wraps them in a read-only, seekable stream for a library that
/// takes a <see cref="Stream"/> (the sandbox allows the System.IO stream types, not the file system).
/// </summary>
/// <param name="Content">The file bytes.</param>
/// <param name="Name">The file name recorded in the handle, if any.</param>
/// <param name="MimeType">The MIME type recorded in the handle, if any.</param>
/// <param name="Size">The size recorded in the handle, in bytes.</param>
/// <param name="ETag">The handle's eTag (SHA-256 of the bytes, lower-case hex, unquoted).</param>
public sealed record ScriptFile(byte[] Content, string? Name, string? MimeType, long Size, string ETag)
{
    /// <summary>A new read-only stream over <see cref="Content"/>, positioned at 0. Each call returns its own stream.</summary>
    public Stream OpenRead() => new MemoryStream(Content, writable: false);
}
