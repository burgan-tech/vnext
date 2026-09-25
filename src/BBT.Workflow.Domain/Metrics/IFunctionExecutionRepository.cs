namespace BBT.Workflow.Metrics;

/// <summary>
/// Filter for reading the function-execution journal — the shared shape behind the paged list and its
/// summary so both apply exactly the same predicate. <see cref="Workflow"/> narrows to a flow-scoped
/// view; <see cref="Succeeded"/> filters by outcome; <see cref="From"/>/<see cref="To"/> bound the
/// window on <c>InvokedAt</c>.
/// </summary>
public sealed record FunctionExecutionQuery(
    string FunctionKey,
    string? Workflow = null,
    DateTime? From = null,
    DateTime? To = null,
    bool? Succeeded = null,
    int Page = 1,
    int PageSize = 20);

/// <summary>One page of execution rows plus whether a further page exists.</summary>
public sealed record FunctionExecutionQueryResult(
    IReadOnlyList<FunctionExecution> Items,
    bool HasNext);

/// <summary>
/// Aggregate over the filtered window: how many runs, the p50/p95 latency and the failure rate —
/// "is this function slow, is it erroring". Percentiles are null when the window is empty.
/// </summary>
public sealed record FunctionExecutionSummary(
    long Count,
    double? P50Ms,
    double? P95Ms,
    double FailureRate);

/// <summary>
/// Read/write access to the function-execution journal (<see cref="FunctionExecution"/>), backing the
/// C1 write and the D metrics endpoints (vnext-client-sdk-core#60). Domain-wide, fixed
/// <c>sys_metrics</c> schema.
/// </summary>
public interface IFunctionExecutionRepository
{
    /// <summary>
    /// Appends a batch of execution rows in a single round-trip. Called by the background journal writer
    /// on its own DI scope (inside its own unit of work), independently of any function request — the
    /// producer only enqueues, never writes. Journaling is best-effort: the writer swallows failures so a
    /// telemetry write can never affect the functions being recorded. A no-op for an empty batch.
    /// </summary>
    Task InsertBatchAsync(IReadOnlyCollection<FunctionExecution> executions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one page of the journal for <see cref="FunctionExecutionQuery.FunctionKey"/>, newest
    /// first, applying the filter. Fetches one row beyond the page to decide
    /// <see cref="FunctionExecutionQueryResult.HasNext"/> without a separate count.
    /// </summary>
    Task<FunctionExecutionQueryResult> QueryAsync(
        FunctionExecutionQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Aggregates count / p50 / p95 / failure-rate over the same filtered window.</summary>
    Task<FunctionExecutionSummary> SummarizeAsync(
        FunctionExecutionQuery query,
        CancellationToken cancellationToken = default);
}
