namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// One part of an HTTP task's <c>multipart/form-data</c> body on the wire between the hosts.
/// A field part carries <see cref="Value"/>; a file part carries <see cref="Content"/>, which JSON
/// serialization writes as base64 when the call leaves the process and reads back as the same bytes.
/// </summary>
public sealed record HttpMultipartPartBinding
{
    /// <summary>The form field name.</summary>
    public required string Name { get; init; }

    /// <summary>The text of a field part; null on a file part.</summary>
    public string? Value { get; init; }

    /// <summary>The bytes of a file part; null on a field part.</summary>
    public byte[]? Content { get; init; }

    /// <summary>The file name of a file part.</summary>
    public string? FileName { get; init; }

    /// <summary>The media type of a file part.</summary>
    public string? ContentType { get; init; }
}
