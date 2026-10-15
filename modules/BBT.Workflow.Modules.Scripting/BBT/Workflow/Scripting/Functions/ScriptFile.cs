namespace BBT.Workflow.Scripting.Functions;

/// <summary>
/// An x-storage file read by a script through <c>ScriptBase.GetFileAsync</c>. The bytes are a plain array: the
/// script sandbox bans <c>System.IO</c>, so no stream is handed out.
/// </summary>
/// <param name="Content">The file bytes.</param>
/// <param name="Name">The file name recorded in the handle, if any.</param>
/// <param name="MimeType">The MIME type recorded in the handle, if any.</param>
/// <param name="Size">The size recorded in the handle, in bytes.</param>
/// <param name="ETag">The handle's eTag (SHA-256 of the bytes, lower-case hex, unquoted).</param>
public sealed record ScriptFile(byte[] Content, string? Name, string? MimeType, long Size, string ETag);
