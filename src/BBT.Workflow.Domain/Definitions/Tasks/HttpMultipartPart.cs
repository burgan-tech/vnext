namespace BBT.Workflow.Definitions;

/// <summary>
/// One part of an HTTP task's <c>multipart/form-data</c> body, built by a mapping script through
/// <see cref="HttpTask.AddMultipartField"/> or <see cref="HttpTask.AddMultipartFile"/>.
/// </summary>
/// <remarks>
/// A part is either a text field (<see cref="Value"/>) or a file (<see cref="Content"/>). A file
/// travels as its own bytes — never through a string — which is the reason the type exists: a body
/// string is sent as UTF-8, so any byte above 0x7F of a PDF or an image would be rewritten on the way.
/// </remarks>
public sealed record HttpMultipartPart
{
    /// <summary>The form field name (the <c>name</c> of the part's Content-Disposition).</summary>
    public required string Name { get; init; }

    /// <summary>The text of a field part; null on a file part.</summary>
    public string? Value { get; init; }

    /// <summary>The bytes of a file part, sent as is; null on a field part.</summary>
    public byte[]? Content { get; init; }

    /// <summary>The file name of a file part (the <c>filename</c> of its Content-Disposition).</summary>
    public string? FileName { get; init; }

    /// <summary>The media type of a file part, e.g. <c>application/pdf</c>.</summary>
    public string? ContentType { get; init; }
}
