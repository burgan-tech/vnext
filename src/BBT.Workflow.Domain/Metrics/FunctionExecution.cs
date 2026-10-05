using BBT.Aether.Domain.Entities;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Metrics;

/// <summary>
/// One execution record of a domain-registered function (vnext-client-sdk-core#60, item C1).
/// </summary>
/// <remarks>
/// A domain-wide journal: one row per invocation that runs through
/// <c>FunctionAppService.ExecuteFunctionAsync</c> — i.e. the functions listed in the domain's
/// <c>sys-catalog → Functions</c> registry. The runtime's built-in instance read functions
/// (<c>state</c>, <c>data</c>, <c>view</c>, <c>schema</c>, <c>tasks</c>, …) bypass that path and are
/// deliberately NOT recorded here; their telemetry lives in APM (the <c>Instance.Read/{kind}</c> and
/// <c>Function.Execute</c> spans). Because a function can run domain-scoped with no flow at all, the
/// row lives in the fixed <c>sys_metrics</c> schema (like the messaging tables in <c>sys_queues</c>),
/// not in a per-flow schema, and <see cref="Workflow"/> / <see cref="InstanceId"/> are nullable —
/// populated only for flow/instance-scoped calls.
/// </remarks>
/// <remarks>
/// <para>
/// This entity deliberately does <b>not</b> implement <c>ICreationAuditedObject</c>. The journal write
/// is asynchronous (a background writer on its own DI scope), so the request <c>ICurrentUser</c> is no
/// longer ambient when the row is saved — Aether's audit interceptor would stamp
/// <see cref="CreatedBy"/>/<see cref="CreatedByBehalfOf"/> as null. Instead the caller (the actor and
/// the behalf-of user) is captured at invocation time and passed to <see cref="Record"/>, which sets
/// those columns explicitly — the same values the interceptor would have stamped
/// (<c>ActorUserName</c> → <see cref="CreatedBy"/>, <c>UserName</c> → <see cref="CreatedByBehalfOf"/>).
/// </para>
/// </remarks>
public sealed class FunctionExecution : Entity<Guid>
{
    /// <summary>
    /// The fixed physical schema this journal lives in. Domain-wide (not per-flow), so every read and
    /// write must pin it explicitly: the context is registered with schema switching, so an operation
    /// running inside another schema scope (a function call runs inside <c>sys_functions</c>) would
    /// otherwise target the ambient schema. Shared by the context's default-schema mapping and the
    /// repository's schema pin.
    /// </summary>
    public const string SchemaName = "sys_metrics";

    private FunctionExecution()
    {
    }

    private FunctionExecution(
        Guid id,
        string domain,
        string functionKey,
        string functionVersion,
        string scope,
        string? workflow,
        Guid? instanceId,
        DateTime invokedAt,
        double durationMs,
        bool succeeded,
        int? statusCode,
        string? errorCode,
        bool fromCache,
        string? invokedBy,
        string? invokedByBehalfOf,
        string? traceId) : base(id)
    {
        Domain = domain;
        FunctionKey = functionKey;
        FunctionVersion = functionVersion;
        Scope = scope;
        Workflow = workflow;
        InstanceId = instanceId;
        InvokedAt = invokedAt;
        DurationMs = durationMs;
        Succeeded = succeeded;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        FromCache = fromCache;
        TraceId = traceId;
        CreatedAt = invokedAt;
        CreatedBy = invokedBy;
        CreatedByBehalfOf = invokedByBehalfOf;
    }

    /// <summary>
    /// Builds a completed execution record. <paramref name="scope"/> stores its <see cref="TaskScope.Code"/>
    /// (D/F/I); <paramref name="workflow"/> and <paramref name="instanceId"/> must be null for a
    /// domain-scoped call, and the workflow set (instance optional) for a flow/instance-scoped one.
    /// <paramref name="invokedBy"/>/<paramref name="invokedByBehalfOf"/> are the caller identity captured
    /// at invocation time (actor and behalf-of), stored in <see cref="CreatedBy"/>/<see cref="CreatedByBehalfOf"/>.
    /// </summary>
    public static FunctionExecution Record(
        Guid id,
        string domain,
        string functionKey,
        string functionVersion,
        TaskScope scope,
        string? workflow,
        Guid? instanceId,
        DateTime invokedAt,
        double durationMs,
        bool succeeded,
        int? statusCode,
        string? errorCode,
        bool fromCache,
        string? invokedBy = null,
        string? invokedByBehalfOf = null,
        string? traceId = null) =>
        new(id, domain, functionKey, functionVersion, scope.Code, workflow, instanceId,
            invokedAt, durationMs, succeeded, statusCode, errorCode, fromCache,
            invokedBy, invokedByBehalfOf, traceId);

    /// <summary>Owning domain.</summary>
    public string Domain { get; private set; } = string.Empty;

    /// <summary>The function definition key that was invoked.</summary>
    public string FunctionKey { get; private set; } = string.Empty;

    /// <summary>The resolved function version.</summary>
    public string FunctionVersion { get; private set; } = string.Empty;

    /// <summary>Scope code: <c>D</c> (domain), <c>F</c> (flow), <c>I</c> (instance).</summary>
    public string Scope { get; private set; } = string.Empty;

    /// <summary>Workflow key — set for flow/instance scope, null for a domain-scoped call.</summary>
    public string? Workflow { get; private set; }

    /// <summary>Owning instance id — set for instance scope, null otherwise.</summary>
    public Guid? InstanceId { get; private set; }

    /// <summary>When the invocation started (UTC). The meaningful ordering/filtering timestamp.</summary>
    public DateTime InvokedAt { get; private set; }

    /// <summary>Wall-clock duration of the invocation in milliseconds.</summary>
    public double DurationMs { get; private set; }

    /// <summary>Whether the invocation succeeded (<c>Result.IsSuccess</c>).</summary>
    public bool Succeeded { get; private set; }

    /// <summary>The function's response status code on success; null when it failed before producing one.</summary>
    public int? StatusCode { get; private set; }

    /// <summary>Error code on failure (<c>Result.Error.Code</c>); null on success.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>Whether the response was served from the function's read-through cache (its tasks were skipped).</summary>
    public bool FromCache { get; private set; }

    /// <summary>
    /// OTel trace id of the invocation (from <c>Activity.Current</c> at write time), for correlating a
    /// row with its full trace in APM/ELK. Null when no ambient activity was recording.
    /// </summary>
    public string? TraceId { get; private set; }

    // CreatedBy / CreatedByBehalfOf are captured at invocation time and set explicitly by Record (see the
    // class remarks) — NOT audit-stamped, because the write is asynchronous and the request ICurrentUser
    // is no longer ambient when the row is saved. CreatedAt is initialized to InvokedAt so it reflects the
    // invocation instant, not the (later) background save instant.

    /// <summary>When the row was created — equal to <see cref="InvokedAt"/> (the invocation instant).</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>The actor that invoked the function (<c>ICurrentUser.ActorUserName</c> at invocation time).</summary>
    public string? CreatedBy { get; private set; }

    /// <summary>The behalf-of caller (<c>ICurrentUser.UserName</c>), when the invocation was made on someone's behalf.</summary>
    public string? CreatedByBehalfOf { get; private set; }
}
