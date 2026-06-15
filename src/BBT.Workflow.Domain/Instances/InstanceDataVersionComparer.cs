using System.Text.RegularExpressions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Compares InstanceData first by semantic version, then by history sequence for entries with the same version.
/// Supports both simple versions (1.0.0) and extended format (MAJOR.MINOR.PATCH[-PRERELEASE]-pkg.PKG_VERSION+PKG_NAME).
/// </summary>
/// <remarks>
/// Version Format: MAJOR.MINOR.PATCH[-PRERELEASE]-pkg.PKG_VERSION+PKG_NAME[+BUILD_METADATA]
/// <list type="bullet">
///     <item><description>1.0.0 or 1.0.0-alpha.1 → Artifact version (from component JSON file, may include pre-release)</description></item>
///     <item><description>-pkg.1.17.0 → Package version (affects SemVer ordering)</description></item>
///     <item><description>+account → Build metadata (package name, doesn't affect ordering)</description></item>
///     <item><description>+build.123 → Additional build metadata (optional, doesn't affect ordering)</description></item>
/// </list>
/// 
/// Supported Examples:
/// <list type="bullet">
///     <item><description>1.0.0-pkg.1.17.0+account - Standard format</description></item>
///     <item><description>2.1.3-pkg.2.5.1+customer - Different package name</description></item>
///     <item><description>1.0.0-alpha.1-pkg.1.17.0+account - Pre-release artifact version</description></item>
///     <item><description>1.0.0-pkg.1.17.0+account+build.123 - Multiple build metadata</description></item>
/// </list>
/// 
/// Comparison order:
/// <list type="number">
///     <item><description>Compare artifact version (MAJOR.MINOR.PATCH[-PRERELEASE])</description></item>
///     <item><description>If equal, compare package version</description></item>
///     <item><description>Build metadata (+PKG_NAME, +build.xxx) is ignored in comparisons</description></item>
/// </list>
/// 
/// Version Resolution (FindBestMatch):
/// <list type="bullet">
///     <item><description>"latest" or null/empty → Returns the highest available version</description></item>
///     <item><description>"1.0.0-pkg.1.17.0+account" (full version) → Exact match only</description></item>
///     <item><description>"1.0.0" (artifact version) → Finds highest pkg version for that artifact</description></item>
///     <item><description>"00.01.417" (package version) → Falls back to matching against package version part of stored versions</description></item>
///     <item><description>"1.0" (partial version) → Finds highest version matching the prefix</description></item>
///     <item><description>"1" (major-only version) → Finds highest version matching the major</description></item>
/// </list>
/// </remarks>
public partial class InstanceDataVersionComparer : IComparer<InstanceData>
{
    /// <summary>
    /// Singleton instance of the comparer.
    /// </summary>
    public static InstanceDataVersionComparer Instance { get; } = new();

    /// <inheritdoc />
    public int Compare(InstanceData? x, InstanceData? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        // First compare by version
        var versionComparison = CompareVersionStrings(x.Version, y.Version);

        // If versions are equal, compare by HistorySequence
        if (versionComparison == 0)
        {
            return x.HistorySequence.CompareTo(y.HistorySequence);
        }

        return versionComparison;
    }

    /// <summary>
    /// Compares two version strings.
    /// Supports both simple versions (1.0.0) and extended format (1.0.0-pkg.1.17.0+account).
    /// </summary>
    /// <param name="v1">First version string</param>
    /// <param name="v2">Second version string</param>
    /// <returns>Negative if v1 &lt; v2, zero if equal, positive if v1 &gt; v2</returns>
    internal static int CompareVersionStrings(string? v1, string? v2)
    {
        if (string.IsNullOrWhiteSpace(v1) && string.IsNullOrWhiteSpace(v2)) return 0;
        if (string.IsNullOrWhiteSpace(v1)) return -1;
        if (string.IsNullOrWhiteSpace(v2)) return 1;

        var parsed1 = ParseVersion(v1);
        var parsed2 = ParseVersion(v2);

        // First compare artifact versions
        var artifactComparison = CompareSemanticVersion(parsed1.ArtifactVersion, parsed2.ArtifactVersion);
        if (artifactComparison != 0)
        {
            return artifactComparison;
        }

        // If artifact versions are equal, compare package versions
        return CompareSemanticVersion(parsed1.PackageVersion, parsed2.PackageVersion);
    }

    /// <summary>
    /// Parses a version string into its components.
    /// </summary>
    /// <param name="version">Version string (e.g., "1.0.0" or "1.0.0-pkg.1.17.0+account")</param>
    /// <returns>Parsed version components</returns>
    public static ParsedVersion ParseVersion(string version)
    {
        // Remove build metadata (+PKG_NAME) as it doesn't affect comparison
        var buildMetadataIndex = version.IndexOf('+');
        var versionWithoutMetadata = buildMetadataIndex >= 0
            ? version[..buildMetadataIndex]
            : version;

        // Try to match extended format: MAJOR.MINOR.PATCH-pkg.PKG_VERSION
        var match = ExtendedVersionRegex().Match(versionWithoutMetadata);
        if (match.Success)
        {
            return new ParsedVersion(
                match.Groups["artifact"].Value,
                match.Groups["package"].Value
            );
        }

        // Simple format: just artifact version
        return new ParsedVersion(versionWithoutMetadata, null);
    }

    /// <summary>
    /// Compares two semantic version strings with proper pre-release support.
    /// </summary>
    /// <remarks>
    /// Follows SemVer spec: pre-release versions have lower precedence than normal versions.
    /// For example: 1.0.0-alpha &lt; 1.0.0-beta &lt; 1.0.0
    /// </remarks>
    private static int CompareSemanticVersion(string? v1, string? v2)
    {
        if (string.IsNullOrWhiteSpace(v1) && string.IsNullOrWhiteSpace(v2)) return 0;
        if (string.IsNullOrWhiteSpace(v1)) return -1;
        if (string.IsNullOrWhiteSpace(v2)) return 1;

        // Parse into core version and pre-release parts
        var (core1, preRelease1) = ParseSemVerParts(v1);
        var (core2, preRelease2) = ParseSemVerParts(v2);

        // Compare core versions first
        var coreComparison = CompareCoreVersion(core1, core2);
        if (coreComparison != 0)
            return coreComparison;

        // Core versions are equal, compare pre-release parts
        // Per SemVer: no pre-release > has pre-release (stable is higher)
        var hasPreRelease1 = !string.IsNullOrEmpty(preRelease1);
        var hasPreRelease2 = !string.IsNullOrEmpty(preRelease2);

        if (!hasPreRelease1 && !hasPreRelease2) return 0;
        if (!hasPreRelease1) return 1;  // v1 is stable, v2 has pre-release → v1 > v2
        if (!hasPreRelease2) return -1; // v2 is stable, v1 has pre-release → v1 < v2

        // Both have pre-release, compare lexicographically
        return string.Compare(preRelease1, preRelease2, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a version string into core version and pre-release parts.
    /// Only treats the dash as pre-release separator if what follows starts with a letter.
    /// This allows versions like "1.0.0" or "1" to be parsed correctly without treating them as pre-release.
    /// </summary>
    private static (string Core, string? PreRelease) ParseSemVerParts(string version)
    {
        var dashIndex = version.IndexOf('-');
        if (dashIndex < 0)
            return (version, null);

        // Check if what follows the dash starts with a letter (pre-release identifier)
        // Otherwise, it might be part of an extended version format like "1.0.0-pkg.1.17.0"
        var afterDash = version[(dashIndex + 1)..];
        if (afterDash.Length > 0 && char.IsLetter(afterDash[0]))
        {
            return (version[..dashIndex], afterDash);
        }

        // Treat as core version without pre-release
        return (version, null);
    }

    /// <summary>
    /// Compares two core version strings (MAJOR.MINOR.PATCH format).
    /// Normalizes incomplete versions (e.g., "1" → "1.0.0", "1.2" → "1.2.0").
    /// Invalid versions are treated as "0.0.0" (lower than any valid version).
    /// </summary>
    private static int CompareCoreVersion(string core1, string core2)
    {
        // Normalize and parse versions
        var normalized1 = NormalizeVersion(core1);
        var normalized2 = NormalizeVersion(core2);

        // Try standard Version parsing
        var parsed1 = Version.TryParse(normalized1, out var version1);
        var parsed2 = Version.TryParse(normalized2, out var version2);

        if (parsed1 && parsed2)
        {
            return version1!.CompareTo(version2);
        }

        // If one is valid and the other is not, invalid is treated as 0.0.0
        if (parsed1 && !parsed2) return 1;  // v1 is valid, v2 is invalid → v1 > v2
        if (!parsed1 && parsed2) return -1; // v1 is invalid, v2 is valid → v1 < v2

        // Both invalid, fall back to string comparison
        return string.Compare(core1, core2, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes a version string to have at least major.minor.patch format.
    /// </summary>
    private static string NormalizeVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => version
        };
    }

    /// <summary>
    /// Strips leading zeros from each dot-separated segment of a version string.
    /// For example, "00.01.417" becomes "0.1.417".
    /// </summary>
    private static string NormalizeLeadingZeros(string version)
    {
        var parts = version.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], out var number))
            {
                parts[i] = number.ToString();
            }
        }
        return string.Join('.', parts);
    }

    /// <summary>
    /// Checks if a version string contains package version information.
    /// </summary>
    /// <param name="version">Version string to check</param>
    /// <returns>True if the version contains "-pkg." indicating package version</returns>
    public static bool HasPackageVersion(string? version)
    {
        return !string.IsNullOrWhiteSpace(version) && version.Contains("-pkg.");
    }

    /// <summary>
    /// Checks if the version is a full version (contains package version).
    /// Alias for <see cref="HasPackageVersion"/>.
    /// </summary>
    /// <param name="version">Version string to check</param>
    /// <returns>True if the version is in full format (e.g., "1.0.0-pkg.1.17.0+account")</returns>
    public static bool IsFullVersion(string? version) => HasPackageVersion(version);

    /// <summary>
    /// Checks if the version is an artifact version format (MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-PRERELEASE).
    /// </summary>
    /// <param name="version">Version string to check</param>
    /// <returns>True if the version matches artifact version format</returns>
    /// <remarks>
    /// Matches:
    /// - 1.0.0
    /// - 1.0.0-alpha.1
    /// - 2.1.3-beta
    /// - 1.0.0-rc.1
    /// Does NOT match:
    /// - 1.0.0-pkg.1.17.0+account (full version)
    /// - 1.0 (partial version)
    /// </remarks>
    public static bool IsArtifactVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        // If it has package version, it's not just an artifact version
        if (HasPackageVersion(version))
            return false;

        // Match MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-PRERELEASE
        return ArtifactVersionRegex().IsMatch(version);
    }

    /// <summary>
    /// Checks if the version is a partial version format (MAJOR.MINOR).
    /// </summary>
    /// <param name="version">Version string to check</param>
    /// <returns>True if the version matches partial version format (e.g., "1.0")</returns>
    public static bool IsPartialVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        return PartialVersionRegex().IsMatch(version);
    }

    /// <summary>
    /// Checks if the version is a major-only version format (single number).
    /// </summary>
    /// <param name="version">Version string to check</param>
    /// <returns>True if the version matches major-only format (e.g., "1", "2", "10")</returns>
    public static bool IsMajorOnlyVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        return MajorOnlyVersionRegex().IsMatch(version);
    }

    /// <summary>
    /// Checks if the version string represents the "latest" keyword.
    /// </summary>
    /// <param name="version">Version string to check</param>
    /// <returns>True if the version is "latest" (case-insensitive)</returns>
    public static bool IsLatestKeyword(string? version)
    {
        return string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if the version request should resolve to the latest available version.
    /// This is true when the version is null, empty, whitespace, or the "latest" keyword.
    /// </summary>
    /// <param name="version">Version string to check</param>
    /// <returns>True if the request should resolve to the latest version</returns>
    /// <remarks>
    /// Use this method to centralize the "latest" resolution logic across the codebase.
    /// </remarks>
    public static bool IsRequestingLatest(string? version)
    {
        return string.IsNullOrWhiteSpace(version) || IsLatestKeyword(version);
    }

    /// <summary>
    /// Checks if a full version matches the given artifact version.
    /// </summary>
    /// <param name="fullVersion">Full version string (e.g., "1.0.0-pkg.1.17.0+account")</param>
    /// <param name="artifactVersion">Artifact version to match (e.g., "1.0.0" or "1.0.0-alpha.1")</param>
    /// <returns>True if the full version's artifact part matches the given artifact version</returns>
    public static bool MatchesArtifact(string? fullVersion, string? artifactVersion)
    {
        if (string.IsNullOrWhiteSpace(fullVersion) || string.IsNullOrWhiteSpace(artifactVersion))
            return false;

        var parsed = ParseVersion(fullVersion);
        return string.Equals(parsed.ArtifactVersion, artifactVersion, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a full version matches the given package version.
    /// Normalizes both versions by stripping leading zeros from each segment before comparison.
    /// </summary>
    /// <param name="fullVersion">Full version string (e.g., "1.0.0-pkg.00.01.417+onboarding")</param>
    /// <param name="packageVersion">Package version to match (e.g., "00.01.417")</param>
    /// <returns>True if the full version's package part matches the given package version</returns>
    /// <example>
    /// MatchesPackage("1.0.0-pkg.00.01.417+onboarding", "00.01.417") → true
    /// MatchesPackage("1.0.0-pkg.1.17.0+account", "1.17.0") → true
    /// MatchesPackage("1.0.0-pkg.00.01.417+onboarding", "0.1.417") → true (normalized match)
    /// </example>
    public static bool MatchesPackage(string? fullVersion, string? packageVersion)
    {
        if (string.IsNullOrWhiteSpace(fullVersion) || string.IsNullOrWhiteSpace(packageVersion))
            return false;

        var parsed = ParseVersion(fullVersion);
        if (parsed.PackageVersion == null)
            return false;

        return string.Equals(
            NormalizeLeadingZeros(parsed.PackageVersion),
            NormalizeLeadingZeros(packageVersion),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a full version matches the given partial version (MAJOR.MINOR format).
    /// Uses explicit MAJOR.MINOR extraction to avoid false positives with multi-digit versions.
    /// </summary>
    /// <param name="fullVersion">Full version string (e.g., "1.0.5-pkg.1.17.0+account")</param>
    /// <param name="partialVersion">Partial version in MAJOR.MINOR format (e.g., "1.0")</param>
    /// <returns>True if the full version's MAJOR.MINOR matches the given partial version</returns>
    /// <remarks>
    /// For major-only version matching (e.g., "1"), use <see cref="MatchesMajor"/> instead.
    /// </remarks>
    /// <example>
    /// MatchesPartial("1.0.5-pkg.1.0.0+account", "1.0") → true
    /// MatchesPartial("1.20.0-pkg.1.0.0+account", "1.2") → false (not a false positive)
    /// MatchesPartial("1.2.3-pkg.1.0.0+account", "1.2") → true
    /// </example>
    public static bool MatchesPartial(string? fullVersion, string? partialVersion)
    {
        if (string.IsNullOrWhiteSpace(fullVersion) || string.IsNullOrWhiteSpace(partialVersion))
            return false;

        var parsed = ParseVersion(fullVersion);

        // Extract MAJOR.MINOR from artifact version
        var parts = parsed.ArtifactVersion.Split('.');
        if (parts.Length < 2)
            return false;

        var artifactMajorMinor = $"{parts[0]}.{parts[1]}";
        return string.Equals(artifactMajorMinor, partialVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if a full version matches the given major version.
    /// Uses explicit major version extraction to avoid false positives with multi-digit versions.
    /// </summary>
    /// <param name="fullVersion">Full version string (e.g., "1.0.5-pkg.1.17.0+account")</param>
    /// <param name="majorVersion">Major version to match (e.g., "1")</param>
    /// <returns>True if the full version's major component equals the given major version</returns>
    /// <example>
    /// MatchesMajor("1.2.3-pkg.1.0.0+account", "1") → true
    /// MatchesMajor("12.0.0-pkg.1.0.0+account", "1") → false (not a false positive)
    /// MatchesMajor("12.0.0-pkg.1.0.0+account", "12") → true
    /// </example>
    public static bool MatchesMajor(string? fullVersion, string? majorVersion)
    {
        if (string.IsNullOrWhiteSpace(fullVersion) || string.IsNullOrWhiteSpace(majorVersion))
            return false;

        var parsed = ParseVersion(fullVersion);
        
        // Extract the major version from artifact (first segment before '.')
        var dotIndex = parsed.ArtifactVersion.IndexOf('.');
        if (dotIndex < 0)
            return false;

        var artifactMajor = parsed.ArtifactVersion[..dotIndex];
        return string.Equals(artifactMajor, majorVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds the best matching version from a list of versions based on the requested version.
    /// </summary>
    /// <param name="versions">Collection of available versions</param>
    /// <param name="requestedVersion">The version requested by the user</param>
    /// <returns>The best matching version, or null if no match found</returns>
    /// <remarks>
    /// Matching logic:
    /// <list type="number">
    ///     <item><description>If requestedVersion is null/empty or "latest" → returns the latest version</description></item>
    ///     <item><description>If requestedVersion is a full version → exact match only</description></item>
    ///     <item><description>If requestedVersion is an artifact version → finds highest pkg version for that artifact; if no artifact match, falls back to package version matching</description></item>
    ///     <item><description>If requestedVersion is a partial version (MAJOR.MINOR) → finds highest version matching the prefix</description></item>
    ///     <item><description>If requestedVersion is a major-only version (e.g., "1") → finds highest version matching the major</description></item>
    /// </list>
    /// </remarks>
    public static string? FindBestMatch(IEnumerable<string> versions, string? requestedVersion)
    {
        var versionList = versions?.ToList() ?? [];
        if (versionList.Count == 0)
            return null;

        // 1. If requestedVersion is null/empty or "latest" → return latest version
        if (IsRequestingLatest(requestedVersion))
        {
            return versionList
                .OrderByDescending(v => v, StringVersionComparer.Instance)
                .FirstOrDefault();
        }

        // 2. Try exact match first
        var exactMatch = versionList.FirstOrDefault(v => 
            string.Equals(v, requestedVersion, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
            return exactMatch;

        // 3. If requestedVersion is a full version and no exact match → not found
        if (IsFullVersion(requestedVersion))
            return null;

        // 4. If requestedVersion is an artifact version → find highest pkg version,
        //    falling back to package-version-only match if no artifact match is found.
        //    This handles cases where the request contains only the package version part
        //    (e.g., "00.01.417" matching "1.0.0-pkg.00.01.417+onboarding").
        if (IsArtifactVersion(requestedVersion))
        {
            var artifactPrefix = $"{requestedVersion}-pkg.";
            var matched = versionList
                .Where(v => v.StartsWith(artifactPrefix, StringComparison.Ordinal) || 
                           MatchesArtifact(v, requestedVersion))
                .OrderByDescending(v => v, StringVersionComparer.Instance)
                .FirstOrDefault();

            if (matched != null)
                return matched;

            // No artifact match found; try matching as a package version
            var packageMatched = versionList
                .Where(v => MatchesPackage(v, requestedVersion))
                .OrderByDescending(v => v, StringVersionComparer.Instance)
                .FirstOrDefault();

            return packageMatched;
        }

        // 5. If requestedVersion is a partial version (MAJOR.MINOR) → find highest version matching prefix
        if (IsPartialVersion(requestedVersion))
        {
            var matched = versionList
                .Where(v => MatchesPartial(v, requestedVersion))
                .OrderByDescending(v => v, StringVersionComparer.Instance)
                .FirstOrDefault();

            return matched;
        }

        // 6. If requestedVersion is a major-only version (e.g., "1") → find highest version matching major
        if (IsMajorOnlyVersion(requestedVersion))
        {
            var matched = versionList
                .Where(v => MatchesMajor(v, requestedVersion))
                .OrderByDescending(v => v, StringVersionComparer.Instance)
                .FirstOrDefault();

            return matched;
        }

        return null;
    }

    /// <summary>
    /// Extracts the artifact version from a full version string.
    /// </summary>
    /// <param name="version">Full version string (e.g., "1.0.0-pkg.1.17.0+account")</param>
    /// <returns>Artifact version (e.g., "1.0.0")</returns>
    public static string GetArtifactVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return version;

        var parsed = ParseVersion(version);
        return parsed.ArtifactVersion;
    }

    /// <summary>
    /// Regex pattern for extended version format: MAJOR.MINOR.PATCH[-PRERELEASE]-pkg.PKG_VERSION
    /// Supports pre-release identifiers like -alpha.1, -beta, -rc.1, etc.
    /// </summary>
    /// <remarks>
    /// Pattern breakdown:
    /// - (?&lt;artifact&gt;...) - Captures the artifact version including optional pre-release
    ///   - \d+\.\d+\.\d+ - Base version (e.g., 1.0.0)
    ///   - (?:-[a-zA-Z0-9]+(?:\.[a-zA-Z0-9]+)*)? - Optional pre-release (e.g., -alpha.1, -beta, -rc.1)
    /// - -pkg\. - Literal separator "-pkg."
    /// - (?&lt;package&gt;\d+\.\d+\.\d+) - Package version (e.g., 1.17.0)
    /// </remarks>
    [GeneratedRegex(@"^(?<artifact>\d+\.\d+\.\d+(?:-[a-zA-Z0-9]+(?:\.[a-zA-Z0-9]+)*)?)-pkg\.(?<package>\d+\.\d+\.\d+)$", RegexOptions.Compiled)]
    private static partial Regex ExtendedVersionRegex();

    /// <summary>
    /// Regex pattern for artifact version format: MAJOR.MINOR.PATCH[-PRERELEASE]
    /// </summary>
    [GeneratedRegex(@"^(\d+\.\d+\.\d+(?:-[a-zA-Z0-9]+(?:\.[a-zA-Z0-9]+)*)?)$", RegexOptions.Compiled)]
    private static partial Regex ArtifactVersionRegex();

    /// <summary>
    /// Regex pattern for partial version format: MAJOR.MINOR
    /// </summary>
    [GeneratedRegex(@"^(\d+)\.(\d+)$", RegexOptions.Compiled)]
    private static partial Regex PartialVersionRegex();

    /// <summary>
    /// Regex pattern for major-only version format: MAJOR (single number)
    /// </summary>
    [GeneratedRegex(@"^(\d+)$", RegexOptions.Compiled)]
    private static partial Regex MajorOnlyVersionRegex();

    /// <summary>
    /// Represents a parsed version with artifact and optional package version.
    /// </summary>
    /// <param name="ArtifactVersion">The artifact version (e.g., "1.0.0")</param>
    /// <param name="PackageVersion">The package version (e.g., "1.17.0"), null if not present</param>
    public readonly record struct ParsedVersion(string ArtifactVersion, string? PackageVersion);

    /// <summary>
    /// String-based version comparer that uses CompareVersionStrings for ordering.
    /// </summary>
    public sealed class StringVersionComparer : IComparer<string>
    {
        /// <summary>
        /// Singleton instance of the string version comparer.
        /// </summary>
        public static StringVersionComparer Instance { get; } = new();

        /// <inheritdoc />
        public int Compare(string? x, string? y) => CompareVersionStrings(x, y);
    }
}