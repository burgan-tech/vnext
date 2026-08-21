using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Join policy for a fan-out batch: how per-item outcomes combine into the task outcome.
/// </summary>
public enum FanOutJoinPolicy
{
    All = 1,
    AllSettled = 2,
    Quorum = 3,
    FirstSuccess = 4
}

/// <summary>
/// FanOut Task Definition — executes a referenced inner task once per item of a
/// runtime-resolved collection, in parallel, and joins the results into a single output.
/// Phase 1 supports inline mode only (mode "durable" is reserved for a later phase and is
/// rejected at parse time). Inner task type restriction is deliberately not enforced here —
/// it is a runtime concern for the executor.
/// </summary>
public sealed class FanOutTask : WorkflowTask
{
    /// <summary>The only supported value of <see cref="Mode"/> in Phase 1.</summary>
    public const string InlineMode = "inline";

    /// <summary>Default value of <see cref="MaxDegreeOfParallelism"/> when not configured.</summary>
    public const int DefaultMaxDegreeOfParallelism = 4;

    /// <summary>Default value of <see cref="ItemTimeoutSeconds"/> when not configured.</summary>
    public const int DefaultItemTimeoutSeconds = 30;

    /// <summary>Default value of <see cref="BatchTimeoutSeconds"/> when not configured.</summary>
    public const int DefaultBatchTimeoutSeconds = 120;

    /// <summary>Default value of <see cref="ResultKey"/> when not configured.</summary>
    public const string DefaultResultKey = "fanOutResults";

    private FanOutTask()
    {
    }

    [JsonConstructor]
    private FanOutTask(JsonElement config) : base(config)
    {
        Type = ((int)TaskType.FanOut).ToString();
    }

    /// <summary>
    /// Execution mode. Phase 1 supports only "inline"; "durable" is reserved and rejected.
    /// </summary>
    public string Mode { get; private set; } = InlineMode;

    /// <summary>
    /// JSONPath (dot-path subset, "$." rooted) into instance data selecting the item collection.
    /// </summary>
    public string? ItemsPath { get; private set; }

    /// <summary>
    /// Optional human-readable noun for ONE item of this batch — <c>"document"</c>, <c>"payment"</c>
    /// — surfaced in the batch's log lines and on each item's trace span.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Purely a readability label. It does <strong>not</strong> take part in input binding: the
    /// default binding is a flat <c>SetBody(item.Value)</c>, so the inner task's script sees the
    /// item value itself under no alias, whatever this is set to. Renaming it can never change what
    /// a script reads — only what an operator reads.
    /// </para>
    /// <para>
    /// Optional, and left null rather than defaulted here: the runtime substitutes a neutral label
    /// when it is absent, and a default stored on the definition would be indistinguishable from an
    /// author who really did type <c>"item"</c>.
    /// </para>
    /// </remarks>
    public string? ItemAlias { get; private set; }

    /// <summary>
    /// Reference to the inner task executed once per item.
    /// </summary>
    public Reference? ItemTask { get; private set; }

    /// <summary>
    /// Maximum number of items executed concurrently.
    /// </summary>
    public int MaxDegreeOfParallelism { get; private set; } = DefaultMaxDegreeOfParallelism;

    /// <summary>
    /// Per-item execution timeout, in seconds.
    /// </summary>
    public int ItemTimeoutSeconds { get; private set; } = DefaultItemTimeoutSeconds;

    /// <summary>
    /// Overall batch execution timeout, in seconds.
    /// </summary>
    public int BatchTimeoutSeconds { get; private set; } = DefaultBatchTimeoutSeconds;

    /// <summary>
    /// How per-item outcomes combine into the task outcome.
    /// </summary>
    public FanOutJoinPolicy JoinPolicy { get; private set; } = FanOutJoinPolicy.AllSettled;

    /// <summary>
    /// Minimum successful items for the Quorum join policy. Required when <see cref="JoinPolicy"/>
    /// is <see cref="FanOutJoinPolicy.Quorum"/>.
    /// </summary>
    public int? MinSuccess { get; private set; }

    /// <summary>
    /// Instance data key the default output writes the item results under.
    /// </summary>
    public string ResultKey { get; private set; } = DefaultResultKey;

    /// <summary>
    /// When true (default) the result list preserves item index order.
    /// </summary>
    public bool Ordered { get; private set; } = true;

    /// <summary>
    /// Per-item error boundary (retry/fallback applied independently to every item).
    /// </summary>
    public ErrorBoundary? ItemErrorBoundary { get; private set; }

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("mode", out var modeEl))
        {
            var mode = modeEl.GetString();
            if (!string.Equals(mode, InlineMode, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"FanOutTask mode '{mode}' is not supported yet. Only '{InlineMode}' is available (Key={Key}).",
                    nameof(config));
            Mode = InlineMode;
        }

        if (config.TryGetProperty("itemsPath", out var itemsPathEl))
        {
            var itemsPath = itemsPathEl.GetString();
            if (string.IsNullOrWhiteSpace(itemsPath) || !itemsPath.StartsWith("$.", StringComparison.Ordinal))
                throw new ArgumentException(
                    $"FanOutTask itemsPath must start with '$.' (Key={Key}).", nameof(config));
            ItemsPath = itemsPath;
        }

        if (config.TryGetProperty("itemAlias", out var aliasEl))
            ItemAlias = aliasEl.GetString();

        if (!config.TryGetProperty("task", out var taskEl))
            throw new ArgumentException($"Property 'task' is required for FanOutTask (Key={Key}).", nameof(config));
        if (taskEl.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Property 'task' must be an object for FanOutTask (Key={Key}).", nameof(config));

        string RequiredTaskProp(string name) =>
            taskEl.TryGetProperty(name, out var el) && el.GetString() is { Length: > 0 } v
                ? v
                : throw new ArgumentException(
                    $"Property 'task.{name}' is required for FanOutTask (Key={Key}).", nameof(config));

        ItemTask = new Reference(
            RequiredTaskProp("key"),
            RequiredTaskProp("domain"),
            RequiredTaskProp("flow"),
            RequiredTaskProp("version"));

        if (config.TryGetProperty("execution", out var execEl))
        {
            if (execEl.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"Property 'execution' must be an object for FanOutTask (Key={Key}).", nameof(config));

            if (execEl.TryGetProperty("maxDegreeOfParallelism", out var dopEl))
                MaxDegreeOfParallelism = dopEl.GetInt32();
            if (execEl.TryGetProperty("itemTimeoutSeconds", out var itemToEl))
                ItemTimeoutSeconds = itemToEl.GetInt32();
            if (execEl.TryGetProperty("batchTimeoutSeconds", out var batchToEl))
                BatchTimeoutSeconds = batchToEl.GetInt32();
        }

        if (MaxDegreeOfParallelism < 1)
            throw new ArgumentException($"FanOutTask maxDegreeOfParallelism must be >= 1 (Key={Key}).", nameof(config));
        if (ItemTimeoutSeconds < 1 || BatchTimeoutSeconds < 1)
            throw new ArgumentException($"FanOutTask timeouts must be positive (Key={Key}).", nameof(config));
        if (ItemTimeoutSeconds > BatchTimeoutSeconds)
            throw new ArgumentException(
                $"FanOutTask itemTimeoutSeconds ({ItemTimeoutSeconds}) cannot exceed batchTimeoutSeconds ({BatchTimeoutSeconds}) (Key={Key}).",
                nameof(config));

        if (config.TryGetProperty("join", out var joinEl))
        {
            if (joinEl.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"Property 'join' must be an object for FanOutTask (Key={Key}).", nameof(config));

            if (joinEl.TryGetProperty("policy", out var policyEl))
            {
                var policyStr = policyEl.GetString();
                if (!Enum.TryParse<FanOutJoinPolicy>(policyStr, ignoreCase: true, out var policy) ||
                    !Enum.IsDefined(policy))
                    throw new ArgumentException(
                        $"FanOutTask join.policy '{policyStr}' is invalid. Expected one of: all, allSettled, quorum, firstSuccess (Key={Key}).",
                        nameof(config));
                JoinPolicy = policy;
            }

            if (joinEl.TryGetProperty("minSuccess", out var minEl))
                MinSuccess = minEl.GetInt32();
            if (joinEl.TryGetProperty("resultKey", out var rkEl) && rkEl.GetString() is { Length: > 0 } rk)
                ResultKey = rk;
            if (joinEl.TryGetProperty("ordered", out var ordEl))
                Ordered = ordEl.GetBoolean();
        }

        if (JoinPolicy == FanOutJoinPolicy.Quorum && MinSuccess is null or < 1)
            throw new ArgumentException(
                $"FanOutTask join.policy 'quorum' requires join.minSuccess >= 1 (Key={Key}).", nameof(config));

        if (config.TryGetProperty("errorBoundary", out var ebEl) && ebEl.ValueKind == JsonValueKind.Object)
            ItemErrorBoundary = ebEl.Deserialize<ErrorBoundary>(JsonSerializerConstants.JsonOptions);
    }

    /// <summary>
    /// Creates a new <see cref="FanOutTask"/> from its JSON configuration.
    /// </summary>
    public static FanOutTask Create(JsonElement config) => new(config);

    /// <summary>
    /// Creates a deep copy of the current task instance.
    /// </summary>
    public override WorkflowTask Clone() => CloneTyped();

    /// <summary>
    /// Creates a typed deep copy of the current <see cref="FanOutTask"/> instance.
    /// </summary>
    public FanOutTask CloneTyped()
    {
        var cloned = new FanOutTask();
        CopyBaseTo(cloned);
        cloned.Mode = Mode;
        cloned.ItemsPath = ItemsPath;
        cloned.ItemAlias = ItemAlias;
        cloned.ItemTask = ItemTask;
        cloned.MaxDegreeOfParallelism = MaxDegreeOfParallelism;
        cloned.ItemTimeoutSeconds = ItemTimeoutSeconds;
        cloned.BatchTimeoutSeconds = BatchTimeoutSeconds;
        cloned.JoinPolicy = JoinPolicy;
        cloned.MinSuccess = MinSuccess;
        cloned.ResultKey = ResultKey;
        cloned.Ordered = Ordered;
        cloned.ItemErrorBoundary = ItemErrorBoundary;
        return cloned;
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling.
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        Mode = InlineMode;
        ItemsPath = null;
        ItemAlias = null;
        ItemTask = null;
        MaxDegreeOfParallelism = DefaultMaxDegreeOfParallelism;
        ItemTimeoutSeconds = DefaultItemTimeoutSeconds;
        BatchTimeoutSeconds = DefaultBatchTimeoutSeconds;
        JoinPolicy = FanOutJoinPolicy.AllSettled;
        MinSuccess = null;
        ResultKey = DefaultResultKey;
        Ordered = true;
        ItemErrorBoundary = null;
    }

    /// <summary>
    /// Creates a new empty instance for object pooling — internal use only.
    /// </summary>
    public static FanOutTask CreateEmpty() => new();
}
