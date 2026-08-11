using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BBT.Aether;
using BBT.Aether.Domain.Entities;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Instance Data
/// </summary>
public sealed class InstanceData : Entity<Guid>, IHasVersion, IHasEtag
{
    private InstanceData()
    {
    }

    internal InstanceData(
        Guid id,
        Guid instanceId,
        string version,
        JsonData data, bool isLatest, int historySequence = 0) : base(id)
    {
        InstanceId = instanceId;
        SetVersion(version);
        Data = data;
        DataHash = ComputeDataHash(data);
        EnteredAt = DateTime.UtcNow;
        ETag = Ulid.NewUlid().ToString();
        IsLatest = isLatest;
        HistorySequence = historySequence;
    }

    /// <summary>
    /// Instance ID
    /// </summary>
    public Guid InstanceId { get; private set; }

    /// <summary>
    /// Semantic version number. There may be more than one version on the runtime.
    /// </summary>
    public string Version { get; private set; }

    /// <summary>
    /// History sequence number (for ordering history entries within the same version)
    /// </summary>
    public int HistorySequence { get; private set; }

    /// <summary>
    /// Instance-global version number. Assigned by the persistence write funnel under the
    /// per-instance <c>FOR UPDATE</c> row lock (head VersionNo + 1) before the row is inserted.
    /// Provides a monotonically increasing sequence per instance for concurrency control,
    /// backed by the unique index <c>UX_InstancesData_Instance_VersionNo</c>.
    /// </summary>
    public long VersionNo { get; internal set; }

    /// <summary>
    /// Indicates if this is the latest data for the instance. Decided by the domain
    /// (semantic-version comparison); the persistence write funnel demotes any stale latest
    /// row under the same <c>FOR UPDATE</c> lock, and the partial unique index
    /// <c>UX_InstancesData_Instance_IsLatest</c> enforces at most one latest per instance.
    /// </summary>
    public bool IsLatest { get; private set; }

    /// <summary>
    /// The version strategy this row was created with (in-memory only, never persisted —
    /// internal getter keeps it out of the EF model). Null for the very first data row and for
    /// explicit-version appends (<c>AddDataWithVersion</c>). The persistence write funnel uses
    /// it to rebase <see cref="Version"/> onto the real database head when the in-memory base
    /// turns out to be stale (a concurrent writer committed in between).
    /// </summary>
    internal VersionStrategy? AppliedVersionStrategy { get; private set; }

    /// <summary>
    /// ETag
    /// </summary>
    public string ETag { get; private set; }

    /// <summary>
    /// SHA1 hash of the data payload for change detection
    /// </summary>
    public string DataHash { get; private set; }

    /// <summary>
    /// <see cref="JsonData"/>
    /// </summary>
    public JsonData Data { get; private set; }

    /// <summary>
    /// Entered at
    /// </summary>
    public DateTime EnteredAt { get; private set; }

    public dynamic? Attributes => Data.JsonElement.ToDynamic();

    private void SetVersion(string version)
    {
        Version = Check.NotNullOrWhiteSpace(version, nameof(Version), WorkflowConstants.MaxVersionLength);
    }

    internal InstanceData NewVersion(
        Guid id,
        JsonData jsonData,
        VersionStrategy versionStrategy,
        int historySequence
    )
    {
        var newVersion = IncrementVersion(Version, versionStrategy);
        var newData = Data.Merge(jsonData);
        IsLatest = false;
        return new InstanceData(
            id,
            InstanceId,
            newVersion,
            newData,
            true,
            historySequence
        )
        {
            AppliedVersionStrategy = versionStrategy
        };
    }

    /// <summary>
    /// Rebases this NEW (not yet persisted) row's semantic version onto the real database head.
    /// Called by the persistence write funnel under the per-instance <c>FOR UPDATE</c> lock when
    /// the in-memory base this row was computed from is stale (a concurrent writer committed a
    /// newer head in between): the row's <see cref="AppliedVersionStrategy"/> is re-applied to
    /// the database head's version string, keeping version strings monotonic and duplicate-free.
    /// No-op when the strategy is unknown (first row / explicit-version append) — those keep
    /// their authored version and are separated by <see cref="VersionNo"/> alone.
    /// </summary>
    /// <param name="dbHeadVersion">Version string of the current latest row in the database.</param>
    /// <param name="dbHeadHistorySequence">HistorySequence of that latest row.</param>
    internal void RebaseVersion(string dbHeadVersion, int dbHeadHistorySequence)
    {
        if (AppliedVersionStrategy is null)
            return;

        // VersionStrategy.None falls through IncrementVersion's switch and returns the head
        // string unchanged — the None line continues on the real head, others bump from it.
        var rebased = IncrementVersion(dbHeadVersion, AppliedVersionStrategy);
        SetVersion(rebased);
        HistorySequence = rebased == dbHeadVersion ? dbHeadHistorySequence + 1 : 0;
    }

    /// <summary>
    /// Computes SHA1 hash of the JSON data for change detection
    /// </summary>
    /// <param name="data">The JSON data to hash</param>
    /// <returns>SHA1 hash as hex string</returns>
    private static string ComputeDataHash(JsonData data)
    {
        using var sha1 = SHA1.Create();

        // Use normalized JSON from JsonData for consistent hashing
        var jsonBytes = Encoding.UTF8.GetBytes(data.NormalizedJson);
        var hashBytes = sha1.ComputeHash(jsonBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    internal InstanceData CreateSnapshot()
    {
        var snapshot = new InstanceData
        {
            Id = Id,
            InstanceId = InstanceId,
            Version = Version,
            HistorySequence = HistorySequence,
            VersionNo = VersionNo,
            IsLatest = IsLatest,
            ETag = ETag,
            DataHash = DataHash,
            Data = new JsonData(Data.Json),
            EnteredAt = EnteredAt
        };

        return snapshot;
    }


    /// <summary>
    /// Checks if the provided JSON data has the same content as this instance's data
    /// </summary>
    /// <param name="jsonData">The JSON data to compare</param>
    /// <returns>True if the data is the same, false otherwise</returns>
    public bool HasSameData(JsonData jsonData)
    {
        var otherHash = ComputeDataHash(jsonData);
        return DataHash.Equals(otherHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Marks this instance data as not the latest version
    /// </summary>
    internal void MarkAsNotLatest()
    {
        IsLatest = false;
    }

    /// <summary>
    /// Increments the version based on the version strategy.
    /// Preserves package version (-pkg.x.y.z) and build metadata (+name) if present.
    /// Pre-release identifiers (e.g., -alpha.1) are dropped when incrementing.
    /// </summary>
    /// <param name="currentVersion">Current version string (e.g., "1.0.0", "1.0.0-alpha.1", or "1.0.0-alpha.1-pkg.1.17.0+account")</param>
    /// <param name="versionStrategy">Strategy for version increment (Major, Minor, Patch)</param>
    /// <returns>Incremented version string with preserved pkg suffix and metadata, but pre-release dropped</returns>
    /// <remarks>
    /// Examples:
    /// <list type="bullet">
    ///     <item><description>1.0.0-pkg.1.17.0+account + Patch → 1.0.1-pkg.1.17.0+account</description></item>
    ///     <item><description>1.0.0-alpha.1-pkg.1.17.0+account + Patch → 1.0.1-pkg.1.17.0+account (pre-release dropped)</description></item>
    ///     <item><description>1.0.0-alpha.1 + Major → 2.0.0</description></item>
    /// </list>
    /// </remarks>
    private static string IncrementVersion(string currentVersion, VersionStrategy versionStrategy)
    {
        // Parse extended version format: MAJOR.MINOR.PATCH[-PRERELEASE][-pkg.PKG_VERSION][+BUILD_METADATA]
        // Pre-release can be: -alpha, -alpha.1, -beta.2, -rc.1, etc. (but NOT -pkg which is reserved)
        // Using negative lookahead (?!pkg\.) to exclude -pkg from pre-release matching
        var match = Regex.Match(currentVersion,
            @"^(?<base>\d+\.\d+\.\d+)(?<prerelease>-(?!pkg\.)[a-zA-Z0-9]+(?:\.[a-zA-Z0-9]+)*)?(?<suffix>-pkg\.\d+\.\d+\.\d+)?(?<metadata>\+.+)?$");

        if (!match.Success)
            return currentVersion;

        var baseVersion = match.Groups["base"].Value;
        // Pre-release is intentionally not preserved when incrementing
        var suffix = match.Groups["suffix"].Success ? match.Groups["suffix"].Value : string.Empty;
        var metadata = match.Groups["metadata"].Success ? match.Groups["metadata"].Value : string.Empty;

        // Parse base version components (MAJOR.MINOR.PATCH)
        var baseMatch = Regex.Match(baseVersion, @"^(\d+)\.(\d+)\.(\d+)$");
        if (!baseMatch.Success)
            return currentVersion;

        int.TryParse(baseMatch.Groups[1].Value, out var major);
        int.TryParse(baseMatch.Groups[2].Value, out var minor);
        int.TryParse(baseMatch.Groups[3].Value, out var patch);

        var newBaseVersion = versionStrategy.Code switch
        {
            "Major" => $"{major + 1}.0.0",
            "Minor" => $"{major}.{minor + 1}.0",
            "Patch" => $"{major}.{minor}.{patch + 1}",
            _ => baseVersion
        };

        // Reconstruct version with preserved pkg suffix and metadata (pre-release dropped)
        return $"{newBaseVersion}{suffix}{metadata}";
    }
}
