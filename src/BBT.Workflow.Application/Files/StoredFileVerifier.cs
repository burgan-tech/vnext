using System.Security.Cryptography;

namespace BBT.Workflow.Files;

/// <summary>What a buffered read of an x-storage object turned out to hold.</summary>
public enum StoredFileState
{
    /// <summary>The object is the file: its length and sha256 match the handle.</summary>
    Intact,

    /// <summary>
    /// The object is the file inside an <c>aws-chunked</c> frame (a Dapr 1.17+ S3 upload stored by a server that does
    /// not decode it); the decoded payload matches the handle and is served instead.
    /// </summary>
    RecoveredFromChunkedFrame,

    /// <summary>Neither the object nor a decoded frame matches the handle: the bytes are not served.</summary>
    Mismatch,
}

/// <summary>The verdict and, unless <see cref="StoredFileState.Mismatch"/>, the bytes that are the file.</summary>
public readonly record struct StoredFileCheck(StoredFileState State, byte[]? Content);

/// <summary>
/// Checks buffered x-storage bytes against the handle the runtime wrote (<c>size</c> + <c>eTag</c> = sha256 of the
/// original). Every buffered read goes through it, so a store that changed the bytes is never served silently
/// (vnext-client-sdk-core#145).
/// </summary>
public static class StoredFileVerifier
{
    /// <summary>Verifies <paramref name="stored"/>, recovering the file from an aws-chunked frame when that is what was stored.</summary>
    public static StoredFileCheck Check(FileHandle handle, byte[] stored)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(stored);

        if (Matches(handle, stored))
            return new StoredFileCheck(StoredFileState.Intact, stored);

        // A frame is always longer than its payload; anything not longer cannot be one.
        if (stored.LongLength > handle.Size
            && AwsChunkedPayload.TryDecode(stored, out var payload)
            && Matches(handle, payload))
            return new StoredFileCheck(StoredFileState.RecoveredFromChunkedFrame, payload);

        return new StoredFileCheck(StoredFileState.Mismatch, null);
    }

    private static bool Matches(FileHandle handle, byte[] bytes)
        => bytes.LongLength == handle.Size
           && string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), handle.ETag, StringComparison.OrdinalIgnoreCase);
}
