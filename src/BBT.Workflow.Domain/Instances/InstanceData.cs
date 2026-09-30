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
        JsonData data, bool isLatest)
        : this(id, instanceId, version, data, InstanceDataView.Of(data), ComputeDataHash(data), isLatest)
    {
    }

    /// <summary>
    /// A row whose stored form differs from what the engine sees: <paramref name="stored"/> carries
    /// <c>x-encryption.type: "encrypt"</c> tokens and <paramref name="view"/> the plaintext. Only the write
    /// funnel builds these; <paramref name="dataHash"/> is keyed there (an unkeyed digest of the plaintext
    /// would sit next to the ciphertext as an offline guessing oracle).
    /// </summary>
    internal InstanceData(
        Guid id,
        Guid instanceId,
        string version,
        JsonData stored,
        InstanceDataView view,
        string dataHash,
        bool isLatest) : base(id)
    {
        InstanceId = instanceId;
        SetVersion(version);
        StoredData = stored;
        _view = view;
        DataHash = dataHash;
        EnteredAt = DateTime.UtcNow;
        ETag = Ulid.NewUlid().ToString();
        IsLatest = isLatest;
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
    /// Line-scoped ordinal: the row's 1-based sequence WITHIN its semantic <see cref="Version"/>
    /// string (each new version line restarts at 1; same-version appends continue their line).
    /// Assigned by the InstanceData write service under the per-instance <c>FOR UPDATE</c> row
    /// lock from the line's current maximum, backed by the unique index
    /// <c>UX_InstancesData_Instance_Version_VersionNo</c>. Cross-line ordering is semantic
    /// (see <see cref="InstanceDataVersionComparer"/>); use <see cref="EnteredAt"/> for
    /// global chronology.
    /// </summary>
    public long VersionNo { get; internal set; }

    /// <summary>
    /// Indicates if this is the latest data for the instance. Decided by the write service
    /// (semantic-version comparison against the head it read under the <c>FOR UPDATE</c> lock),
    /// which demotes any stale latest row under that same lock; the partial unique index
    /// <c>UX_InstancesData_Instance_IsLatest</c> enforces at most one latest per instance.
    /// </summary>
    public bool IsLatest { get; private set; }

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
    /// <summary>
    /// The row's content as the engine sees it — plaintext, with every <c>x-encryption.type: "encrypt"</c>
    /// token opened. Not mapped: EF persists <see cref="StoredData"/> only, so no tracking state (Add, fixup,
    /// change detection, a detached graph <c>Update</c>) can ever write this plaintext back to the column.
    /// </summary>
    public JsonData Data => View.Plain;

    /// <summary>
    /// The row exactly as stored in the <c>"Data"</c> column: tokens at encrypted paths, plaintext elsewhere.
    /// The only EF-mapped member of the content. Read it only where the stored form is meant (the write
    /// funnel, the read path's token lookup, SQL-side predicates) — never to feed the engine.
    /// </summary>
    public JsonData StoredData { get; private set; } = null!;

    private IInstanceDataProtector? _protector;
    private string? _schema;
    private InstanceDataView? _view;

    /// <summary>
    /// Plaintext view plus the stored tokens. Built on first access: a materialized row opens its tokens
    /// through the protector the EF interceptor attached; a row without tokens is its own view. A row
    /// carrying tokens with no protector attached (a host without the keyring) keeps the tokens as opaque
    /// strings — never plaintext, and the pipeline gate refuses to run on them.
    /// </summary>
    internal InstanceDataView View
    {
        get
        {
            // Reference publish is atomic; two racing first reads compute the same view.
            var view = _view;
            if (view is not null)
                return view;

            view = _protector is not null && EncryptedValueFormat.MayContainToken(StoredData.Json)
                ? _protector.Unprotect(_schema, InstanceId, StoredData)
                : InstanceDataView.Of(StoredData);
            _view = view;
            return view;
        }
    }

    /// <summary>
    /// Dot paths of this row whose <c>x-encryption</c> token could not be opened (unknown key id, failed
    /// authentication). Forces the plaintext view. Non-empty means the engine must not run on this row.
    /// </summary>
    public IReadOnlySet<string> UndecryptablePaths => View.Undecryptable;

    /// <summary>
    /// Stored token per decrypted dot path — what a caller outside the <c>x-encryption.roles</c> exemption
    /// list is served. Empty for a row without tokens.
    /// </summary>
    public IReadOnlyDictionary<string, string> StoredTokens => View.Tokens;

    /// <summary>
    /// Hands the row the protector that opens its tokens and the flow schema it was read from (the schema holds the
    /// instance's secret). Called by the EF materialization interceptor for every materialized row — the owned content
    /// is not assigned yet at that point, so decryption is deferred to the first <see cref="Data"/> read. First
    /// attachment wins.
    /// </summary>
    internal void AttachProtector(IInstanceDataProtector protector, string? schema)
    {
        if (_protector is not null)
            return;
        _protector = protector;
        _schema = schema;
    }

    /// <summary>
    /// Entered at
    /// </summary>
    public DateTime EnteredAt { get; private set; }

    private dynamic? _attributes;

    /// <summary>
    /// Row-scoped memo: this row is immutable once constructed, so its dynamic attribute tree is
    /// materialized at most once and shared across every subsequent read. Benign race: concurrent
    /// first accesses may each build a tree — content-equivalent, last write wins; a mutation made
    /// on the losing tree during that first-access window is not observed by later readers (the
    /// shared-tree mutation-visibility contract starts once the field is published).
    /// </summary>
    public dynamic? Attributes => _attributes ??= Data.JsonElement.ToDynamic();

    private void SetVersion(string version)
    {
        Version = Check.NotNullOrWhiteSpace(version, nameof(Version), WorkflowConstants.MaxVersionLength);
    }

    /// <summary>
    /// Computes SHA1 hash of the JSON data for change detection. Internal so the InstanceData
    /// write service can run the no-change dedup comparison under the database row lock with
    /// exactly the same normalization as the stored <see cref="DataHash"/>.
    /// </summary>
    /// <param name="data">The JSON data to hash</param>
    /// <returns>SHA1 hash as hex string</returns>
    internal static string ComputeDataHash(JsonData data)
    {
        using var sha1 = SHA1.Create();

        // Use normalized JSON from JsonData for consistent hashing
        var jsonBytes = Encoding.UTF8.GetBytes(data.NormalizedJson);
        var hashBytes = sha1.ComputeHash(jsonBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Wrapper snapshot: copies the scalar row fields (so <see cref="MarkAsNotLatest"/> on either
    /// side stays isolated — <see cref="IsLatest"/>/<see cref="VersionNo"/> are mutated on the
    /// row) while SHARING the immutable <see cref="JsonData"/> by reference. The payload is the
    /// expensive part (parse/normalize memos live on it) and it never changes after construction,
    /// so per-snapshot re-parsing bought nothing.
    /// </summary>
    internal InstanceData CreateSnapshot()
    {
        var snapshot = new InstanceData
        {
            Id = Id,
            InstanceId = InstanceId,
            Version = Version,
            VersionNo = VersionNo,
            IsLatest = IsLatest,
            ETag = ETag,
            DataHash = DataHash,
            StoredData = StoredData,
            _protector = _protector,
            _schema = _schema,
            _view = _view,
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
    internal static string IncrementVersion(string currentVersion, VersionStrategy versionStrategy)
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
