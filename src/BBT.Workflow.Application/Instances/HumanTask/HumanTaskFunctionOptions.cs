using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Instances.HumanTask;

/// <summary>
/// Bounds for the <c>human-task</c> domain function. The endpoint fans out over every workflow
/// schema in the domain and morph-idm multiplies that by every registered domain, so the response
/// was unbounded end to end: no LIMIT in the per-schema query, no cap on the merge, no paging in
/// the contract.
/// </summary>
/// <remarks>
/// These bound the SORT and the RESPONSE, not the scan. Under a sequential-scan plan the database
/// still reads every row of every schema before the limit applies; the limit is not a fix for scan
/// cost and must not be recorded as one.
/// </remarks>
public sealed class HumanTaskFunctionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "HumanTaskFunction";

    /// <summary>
    /// Maximum candidate rows read from one workflow schema, enforced in SQL. Applied before
    /// authorization, so the caller's own list can be shorter.
    /// </summary>
    [Range(1, 5_000, ErrorMessage = "PerSchemaLimit must be between 1 and 5000")]
    public int PerSchemaLimit { get; set; } = 200;

    /// <summary>
    /// Maximum rows in the merged response, applied after authorization and ordering.
    /// </summary>
    [Range(1, 10_000, ErrorMessage = "ResultCap must be between 1 and 10000")]
    public int ResultCap { get; set; } = 500;

    /// <summary>
    /// How many SubFlow levels a single candidate may be walked down before the descent gives up
    /// and reports it unresolved.
    /// </summary>
    /// <remarks>
    /// A bound rather than a guess at real nesting: each level is one query or one remote call, and
    /// a correlation graph that somehow cycles would otherwise walk forever on a public read path.
    /// Exceeding it is logged and counted, never silently treated as "no task here".
    /// </remarks>
    [Range(1, 100, ErrorMessage = "MaxDescentDepth must be between 1 and 100")]
    public int MaxDescentDepth { get; set; } = 10;

    /// <summary>
    /// How many flows one candidate-scan statement covers. The scan reads every flow of the domain
    /// in a single <c>UNION ALL</c> on a single connection; this bounds how large that statement
    /// gets before it is split into further batches on the same connection.
    /// </summary>
    /// <remarks>
    /// Statement size and plan-cache churn, not connections: every batch shares the one connection,
    /// so raising or lowering this changes round trips, never concurrency. A domain with fewer flows
    /// than this issues exactly one statement, which is the common case.
    /// <para>
    /// Validated at startup, because the batching loop advances by this value: a configured 0 or a
    /// negative would leave the offset where it was and spin forever on a public read path, holding
    /// a connection open, with no error to find. A boot failure is the only acceptable way for that
    /// value to surface. The upper bound is measured territory — 400 arms planned in 49.8 ms —
    /// not a hard limit of the database.
    /// </para>
    /// </remarks>
    [Range(1, 1_000, ErrorMessage = "FlowsPerScanStatement must be between 1 and 1000")]
    public int FlowsPerScanStatement { get; set; } = 64;

    /// <summary>
    /// How many candidate-bearing flows are DESCENDED at once when the response has to be rebuilt.
    /// </summary>
    /// <remarks>
    /// This no longer covers the scan. The scan used to be the fan-out — one branch per published
    /// flow, each holding its own unit of work and therefore its own connection — which made this
    /// value the endpoint's ceiling on pooled connections, paid on EVERY request regardless of how
    /// much work there was. Concurrent callers multiplied it (each caller has their own cache key,
    /// so single-flight collapses nothing) and exhausted the server's connection slots: measured at
    /// 20 concurrent distinct callers over a 45-flow domain, 13 answered <c>53300 sorry, too many
    /// clients already</c>. The scan is now one statement on one connection and does not use this.
    /// <para>
    /// What is left is the descent, which is where the fan-out belongs: it walks into other flows
    /// and other domains, and it only runs for flows that actually produced a candidate — a much
    /// narrower set than "every flow that exists". The ceiling on connections is therefore
    /// <c>1 + min(this, flows with work)</c>, and in a typical domain the second term is small.
    /// Raising it still trades connections for latency; measure before doing so in an environment
    /// that shares a pool with the pipeline.
    /// </para>
    /// </remarks>
    [Range(1, 256, ErrorMessage = "FanoutParallelism must be between 1 and 256")]
    public int FanoutParallelism { get; set; } = 10;

    /// <summary>
    /// Process-wide ceiling on concurrent descent branches, across ALL in-flight requests.
    /// </summary>
    /// <remarks>
    /// <see cref="FanoutParallelism"/> bounds one request; this bounds their product, which is what
    /// actually meets the connection pool. Keep it comfortably above <see cref="FanoutParallelism"/>
    /// so a single request is never throttled, and below the pool's size so this endpoint cannot
    /// starve the write path. Enforced by <see cref="HumanTaskDescentLimiter"/>.
    /// </remarks>
    [Range(1, 1_024, ErrorMessage = "MaxConcurrentDescents must be between 1 and 1024")]
    public int MaxConcurrentDescents { get; set; } = 32;
}
