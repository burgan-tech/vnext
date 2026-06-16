namespace BBT.Workflow.Components.Dtos;

/// <summary>
/// Decoded mapping (script-library) code together with its identification metadata.
/// </summary>
public sealed class MappingCodeDto
{
    /// <summary>The mapping component key.</summary>
    public required string Key { get; init; }

    /// <summary>The domain the mapping belongs to.</summary>
    public required string Domain { get; init; }

    /// <summary>The mapping version (SemVer).</summary>
    public required string Version { get; init; }

    /// <summary>The original storage encoding of the code (e.g. <c>Base64</c> or <c>Native</c>).</summary>
    public required string Encoding { get; init; }

    /// <summary>The decoded <c>.csx</c> source code (UTF-8 plain text).</summary>
    public required string Code { get; init; }
}
