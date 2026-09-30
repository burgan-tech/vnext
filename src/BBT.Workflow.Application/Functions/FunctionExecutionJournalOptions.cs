namespace BBT.Workflow.Functions;

/// <summary>
/// Tunables for the asynchronous function-execution journal (vnext-client-sdk-core#60). Bound from the
/// <c>Workflow:FunctionExecutionJournal</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BatchSize"/> is read per drain cycle through <c>IOptionsMonitor</c>, so a config reload
/// takes effect on the next batch with no restart (where the deployment delivers config as a reloadable
/// file — see docs/runtime/function-execution-metrics.md). <see cref="QueueCapacity"/> is read once, when
/// the bounded channel is created at startup: a channel's bound cannot be resized at runtime, so a change
/// to it applies only after the process restarts.
/// </para>
/// </remarks>
public sealed class FunctionExecutionJournalOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Workflow:FunctionExecutionJournal";

    /// <summary>
    /// Bounded in-memory queue capacity. When full, the newest record is dropped (best-effort). Read once
    /// at channel creation — changing it needs a process restart. Default 50,000 (~10 MB worst case).
    /// </summary>
    public int QueueCapacity { get; set; } = 50_000;

    /// <summary>
    /// Maximum rows persisted per <c>SaveChanges</c>. The main throughput lever: fewer, larger batches mean
    /// fewer round-trips. Re-read each drain cycle, so it is tunable at runtime. Default 1,000.
    /// </summary>
    public int BatchSize { get; set; } = 1_000;
}
