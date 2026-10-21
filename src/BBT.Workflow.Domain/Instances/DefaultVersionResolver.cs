namespace BBT.Workflow.Instances;

/// <summary>
/// Which rule produced a version resolution.
/// </summary>
public enum VersionResolutionSource
{
    /// <summary>No default pointer: the floating match, exactly as before the feature.</summary>
    Automatic,

    /// <summary>A full or artifact version was requested; the default ceiling does not apply.</summary>
    Explicit,

    /// <summary>The request was "latest"/empty and the default pointer decided.</summary>
    Default,

    /// <summary>A major or minor floating request, capped at the effective default.</summary>
    Ceiling
}

/// <summary>
/// Why a version resolution did or did not select a version.
/// </summary>
public enum VersionResolutionOutcome
{
    /// <summary>A version was selected.</summary>
    Resolved,

    /// <summary>No published version matches the request.</summary>
    NotFound,

    /// <summary>A matching version exists but lies above the default ceiling.</summary>
    NotActivated,

    /// <summary>The default pointer matches no published version (only for a "latest"/empty or floating request).</summary>
    PointerInvalid
}

/// <summary>
/// Outcome of <see cref="DefaultVersionResolver.Resolve"/>.
/// </summary>
/// <param name="Version">The selected version; null unless the outcome is <see cref="VersionResolutionOutcome.Resolved"/>.</param>
/// <param name="Outcome">Whether a version was selected, and why not otherwise.</param>
/// <param name="Source">The rule that decided.</param>
/// <param name="EffectiveDefault">The version the pointer resolved to; null when there is no pointer or it matches no published version.</param>
public readonly record struct DefaultVersionResolution(
    string? Version,
    VersionResolutionOutcome Outcome,
    VersionResolutionSource Source,
    string? EffectiveDefault);

/// <summary>
/// Pure resolver for the per-flow default version: the pointer decides which version a
/// "latest" or floating request starts with, while versions above it stay reachable
/// only through an explicit (full or artifact) version.
/// </summary>
/// <remarks>
/// Matching itself is delegated to <see cref="InstanceDataVersionComparer"/>; no flow without a
/// pointer behaves differently from <see cref="InstanceDataVersionComparer.FindBestMatch"/>.
/// </remarks>
public static class DefaultVersionResolver
{
    /// <summary>
    /// Resolves the version a request maps to under the flow's default-version pointer.
    /// </summary>
    /// <remarks>
    /// The pointer's FORM is NOT validated here: callers must gate it with <c>IsValidPointer</c> first (an
    /// invalid pointer is a configuration conflict, not something to resolve around). Explicit always wins: a
    /// full or artifact request resolves before the pointer is matched, so it is served even while the pointer
    /// matches no published version; only "latest"/empty and floating requests answer
    /// <see cref="VersionResolutionOutcome.PointerInvalid"/> then.
    /// </remarks>
    /// <param name="published">All published versions of the flow.</param>
    /// <param name="pointer">The flow's default-version pointer, or null when none is set.</param>
    /// <param name="requested">The requested version (null, "latest", partial, artifact or full).</param>
    public static DefaultVersionResolution Resolve(
        IReadOnlyCollection<string> published,
        string? pointer,
        string? requested)
    {
        if (pointer is null)
        {
            return Create(
                InstanceDataVersionComparer.FindBestMatch(published, requested),
                VersionResolutionSource.Automatic,
                effectiveDefault: null);
        }

        var effectiveDefault = InstanceDataVersionComparer.FindBestMatch(published, pointer);

        // Explicit always wins: a full or artifact request is checked before the pointer is judged, so it
        // still resolves while the pointer matches nothing (the way out of a broken pointer).
        if (InstanceDataVersionComparer.IsFullVersion(requested)
            || InstanceDataVersionComparer.IsArtifactVersion(requested))
        {
            return Create(
                InstanceDataVersionComparer.FindBestMatch(published, requested),
                VersionResolutionSource.Explicit,
                effectiveDefault);
        }

        if (effectiveDefault is null)
        {
            return new DefaultVersionResolution(
                null, VersionResolutionOutcome.PointerInvalid, VersionResolutionSource.Default, null);
        }

        if (InstanceDataVersionComparer.IsRequestingLatest(requested))
        {
            return new DefaultVersionResolution(
                effectiveDefault, VersionResolutionOutcome.Resolved, VersionResolutionSource.Default, effectiveDefault);
        }

        var eligible = published
            .Where(v => InstanceDataVersionComparer.StringVersionComparer.Instance.Compare(v, effectiveDefault) <= 0)
            .ToList();
        var capped = InstanceDataVersionComparer.FindBestMatch(eligible, requested);
        if (capped is not null)
        {
            return Create(capped, VersionResolutionSource.Ceiling, effectiveDefault);
        }

        // Nothing under the ceiling: tell "exists but not activated" apart from "does not exist".
        var outcome = InstanceDataVersionComparer.FindBestMatch(published, requested) is not null
            ? VersionResolutionOutcome.NotActivated
            : VersionResolutionOutcome.NotFound;
        return new DefaultVersionResolution(null, outcome, VersionResolutionSource.Ceiling, effectiveDefault);
    }

    /// <summary>
    /// Whether <paramref name="pointer"/> is an accepted default-version value: null (clears the
    /// pointer) or a major, minor, artifact or full version. "latest" and blank are rejected.
    /// </summary>
    public static bool IsValidPointer(string? pointer)
    {
        if (pointer is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(pointer) || InstanceDataVersionComparer.IsLatestKeyword(pointer))
        {
            return false;
        }

        return InstanceDataVersionComparer.IsMajorOnlyVersion(pointer)
               || InstanceDataVersionComparer.IsPartialVersion(pointer)
               || InstanceDataVersionComparer.IsArtifactVersion(pointer)
               || InstanceDataVersionComparer.IsFullVersion(pointer);
    }

    private static DefaultVersionResolution Create(
        string? version,
        VersionResolutionSource source,
        string? effectiveDefault) =>
        new(
            version,
            version is null ? VersionResolutionOutcome.NotFound : VersionResolutionOutcome.Resolved,
            source,
            effectiveDefault);
}
