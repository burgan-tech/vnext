using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BBT.Aether;
using BBT.Workflow.Definitions;
using BBT.Workflow.Metrics;

namespace BBT.Workflow.Functions;

/// <summary>
/// Input for the function-metrics read (vnext-client-sdk-core#60, item D). <see cref="Workflow"/> is
/// null for the domain-scoped endpoint and set for the flow-scoped sibling.
/// </summary>
public sealed class GetFunctionMetricsInput : IHasDomain
{
    /// <summary>Upper bound applied to <see cref="PageSize"/>.</summary>
    public const int MaxPageSize = 100;

    [Required]
    [StringLength(WorkflowConstants.MaxDomainLength)]
    public string Domain { get; set; } = string.Empty;

    /// <summary>Workflow key — null narrows to the domain-scoped view; set narrows to a flow.</summary>
    [StringLength(WorkflowConstants.MaxFlowLength)]
    public string? Workflow { get; set; }

    [Required]
    public string FunctionKey { get; set; } = string.Empty;

    /// <summary>1-based page number (default 1).</summary>
    public int Page { get; set; } = 1;

    /// <summary>Page size (default 20, max 100).</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>Lower bound (inclusive) on <c>invokedAt</c>.</summary>
    public DateTime? From { get; set; }

    /// <summary>Upper bound (inclusive) on <c>invokedAt</c>.</summary>
    public DateTime? To { get; set; }

    /// <summary>Filter by outcome: true = only succeeded, false = only failed, null = both.</summary>
    public bool? Succeeded { get; set; }
}

/// <summary>
/// One execution row of a domain function. Metadata only — no request/response payloads (the
/// runtime never journals a function's payloads for the same auth-material reason as the task journal).
/// </summary>
public sealed class FunctionExecutionItemDto
{
    /// <summary>Unique execution identifier.</summary>
    public Guid ExecutionId { get; set; }

    /// <summary>The resolved function version that ran — lets a reader compare latency across versions of one key.</summary>
    public string FunctionVersion { get; set; } = string.Empty;

    /// <summary>When the invocation started (UTC).</summary>
    public DateTime InvokedAt { get; set; }

    /// <summary>Wall-clock duration in milliseconds.</summary>
    public double DurationMs { get; set; }

    /// <summary>Function scope: <c>D</c> (domain), <c>F</c> (flow), <c>I</c> (instance).</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Workflow key — present for flow/instance scope, null for a domain-scoped call.</summary>
    public string? Workflow { get; set; }

    /// <summary>Owning instance id — present for instance scope, null otherwise.</summary>
    public Guid? InstanceId { get; set; }

    /// <summary>
    /// Whether the invocation succeeded. A function has no task-style <c>businessStatus</c>; the outcome
    /// is this flag plus <see cref="Status"/>, <see cref="StatusCode"/> and (on failure) <see cref="Error"/>.
    /// </summary>
    public bool Succeeded { get; set; }

    /// <summary>
    /// Platform outcome as a string — <c>completed</c> or <c>faulted</c> — derived from
    /// <see cref="Succeeded"/>, mirroring the task journal's <c>status</c> vocabulary so a client that
    /// consumed the transition/task metrics shape reads one grammar. (A function has no separate
    /// <c>businessStatus</c> axis — that is deliberately absent.)
    /// </summary>
    public string Status => Succeeded ? "completed" : "faulted";

    /// <summary>The function's response status code on success; null when it failed before producing one.</summary>
    public int? StatusCode { get; set; }

    /// <summary>Error code on failure; null on success. Never a stack trace.</summary>
    public string? Error { get; set; }

    /// <summary>Whether the response was served from the function's read-through cache (its tasks were skipped).</summary>
    public bool FromCache { get; set; }

    /// <summary>OTel trace id of the invocation, to open its full trace in APM/ELK. Null when nothing was recording.</summary>
    public string? TraceId { get; set; }

    /// <summary>The caller that invoked the function.</summary>
    public string? InvokedBy { get; set; }

    /// <summary>The behalf-of caller, when invoked on someone's behalf.</summary>
    public string? InvokedByBehalfOf { get; set; }

    public static FunctionExecutionItemDto FromEntity(FunctionExecution e) => new()
    {
        ExecutionId = e.Id,
        FunctionVersion = e.FunctionVersion,
        InvokedAt = e.InvokedAt,
        DurationMs = e.DurationMs,
        Scope = e.Scope,
        Workflow = e.Workflow,
        InstanceId = e.InstanceId,
        Succeeded = e.Succeeded,
        StatusCode = e.StatusCode,
        Error = e.ErrorCode,
        FromCache = e.FromCache,
        TraceId = e.TraceId,
        InvokedBy = e.CreatedBy,
        InvokedByBehalfOf = e.CreatedByBehalfOf
    };
}

/// <summary>Aggregate over the filtered window — "is this function slow, is it erroring".</summary>
public sealed class FunctionMetricsSummaryDto
{
    /// <summary>Number of executions in the window.</summary>
    public long Count { get; set; }

    /// <summary>Median (p50) duration in ms; null when the window is empty.</summary>
    public double? P50Ms { get; set; }

    /// <summary>p95 duration in ms; null when the window is empty.</summary>
    public double? P95Ms { get; set; }

    /// <summary>Fraction of executions that failed (0..1).</summary>
    public double FailureRate { get; set; }

    public static FunctionMetricsSummaryDto FromSummary(FunctionExecutionSummary s) => new()
    {
        Count = s.Count,
        P50Ms = s.P50Ms,
        P95Ms = s.P95Ms,
        FailureRate = s.FailureRate
    };
}

/// <summary>
/// One page of a function's execution history plus the window summary — the paged
/// <c>{ links, items, summary }</c> envelope (same paging shape as the instance queries).
/// </summary>
public sealed class GetFunctionMetricsOutput
{
    /// <summary>HATEOAS pagination links (self / first / next / prev).</summary>
    [JsonPropertyName("links")]
    public object? Links { get; set; }

    /// <summary>Execution rows on this page, newest first.</summary>
    [JsonPropertyName("items")]
    public List<FunctionExecutionItemDto> Items { get; set; } = [];

    /// <summary>Aggregate over the whole filtered window (not just this page).</summary>
    [JsonPropertyName("summary")]
    public FunctionMetricsSummaryDto? Summary { get; set; }
}
