namespace BBT.Workflow.Security;

/// <summary>
/// Thrown when instance-data encryption or decryption cannot be completed: no active key, an
/// unloadable key id, a malformed marker, or a ciphertext that fails authentication.
/// </summary>
/// <remarks>
/// Always fails loudly. The alternatives are both worse than an error: returning the marker leaks
/// ciphertext to a client, and returning a placeholder silently corrupts the instance's data on the
/// next full-merge append.
/// </remarks>
public sealed class SensitiveDataEncryptionException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Diagnostic message. Must not contain plaintext.</param>
    public SensitiveDataEncryptionException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    /// <param name="message">Diagnostic message. Must not contain plaintext.</param>
    /// <param name="innerException">Underlying cryptographic failure.</param>
    public SensitiveDataEncryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
