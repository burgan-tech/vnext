using System.Diagnostics;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Business-level spans for caller-role resolution.
/// <para>
/// The outbound call to an external provider already produces an HTTP client span (Aether enables
/// <c>AddHttpClientInstrumentation</c>), so this is not about making the request visible. It is about
/// making the DECISION visible: which provider answered, for which caller, how many roles came back,
/// and — the part no other span can show — whether this surface triggered a call at all or was served
/// the request-scope memo.
/// </para>
/// <para>
/// A span is emitted on BOTH paths, hit and miss, with the outcome in a tag. That is deliberate and
/// follows the compile cache, which is the one cache in this runtime that traces correctly: a
/// hit-only-silence design makes a memoized read indistinguishable from a read that never happened,
/// and the whole point of the memo is that N surfaces share one call. Counting the spans against the
/// HTTP client spans is how you verify the one-call-per-request guarantee actually holds in
/// production rather than only in the unit test.
/// </para>
/// <para>
/// Only providers that DO work are instrumented. The default provider reads <c>ICurrentUser.Roles</c>
/// in-process and is not memoized, so it would emit a span at every authorization surface — ten spans
/// per request that all say the same thing and none of which can be slow.
/// </para>
/// </summary>
public static class AuthorizationActivityHelper
{
    /// <summary>
    /// The source name as a compile-time constant.
    /// <para>
    /// Anything that needs to identify this source — a test's <c>ShouldListenTo</c> predicate above
    /// all — must use THIS and not <c>ActivitySource.Name</c>. Reading the field runs this class's
    /// static initializer, and <c>ActivitySource.AddActivityListener</c> invokes every registered
    /// predicate while it constructs sources: a predicate that touches the field re-enters the
    /// initializer that is still running, reads a null field, and poisons the type for the rest of
    /// the process. A const is inlined by the compiler and triggers nothing.
    /// </para>
    /// </summary>
    public const string SourceName = TelemetryConstants.ActivitySources.Authorization;

    /// <summary>
    /// ActivitySource for authorization operations. Hosts that register sources explicitly must list
    /// <see cref="SourceName"/> in <c>Otel:Tracing:AdditionalSources</c>; a source missing from one
    /// host's list goes dark in that host only, which is the failure mode this convention exists to
    /// prevent.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>Resolution of the caller's role set through the configured provider.</summary>
    public const string OperationResolveRoles = "Auth.ResolveRoles";

    /// <summary>
    /// Starts the role-resolution span.
    /// <para>
    /// Named without a subject suffix, unlike <c>Cache.Get/{key}</c> and <c>Lock.Acquire/{key}</c>.
    /// The subject here is the caller, and putting an identity in a span name would give APM one
    /// operation per user — the provider and the identity go in tags instead, where they are
    /// queryable without fragmenting the aggregation.
    /// </para>
    /// </summary>
    public static Activity? StartResolveRoles(string provider)
    {
        // Implicit parent — see InstanceReadActivityHelper.StartDescend for why the explicit
        // ActivityContext overload is the wrong one: it severs the baggage chain for children.
        var activity = ActivitySource.StartActivity(
            OperationResolveRoles,
            ActivityKind.Internal);

        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        activity?.SetTag(TelemetryConstants.TagNames.AuthProvider, provider);
        return activity;
    }

    /// <summary>
    /// Records the identity the provider was asked about. Kept separate from
    /// <see cref="StartResolveRoles"/> because a memo hit answers without ever reading it.
    /// </summary>
    public static void SetCaller(Activity? activity, string? subject, string? actor, string? position)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.Sub, subject);
        activity.SetTag(TelemetryConstants.TagNames.ActSub, actor);
        activity.SetTag(TelemetryConstants.TagNames.AuthPosition, position);
    }

    /// <summary>
    /// Records a successful resolution.
    /// </summary>
    /// <param name="activity">The span.</param>
    /// <param name="roleCount">How many roles the caller holds.</param>
    /// <param name="memoHit">
    /// True when the request-scope memo answered and no provider call was made. This is the tag the
    /// span exists for.
    /// </param>
    /// <param name="emptyReason">
    /// For an empty answer, which shape it took (<see cref="TelemetryConstants.AuthEmptyReasons"/>). Ignored when roles
    /// came back.
    /// </param>
    public static void SetResolved(Activity? activity, int roleCount, bool memoHit, string? emptyReason = null)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.AuthMemoHit, memoHit);
        activity.SetTag(TelemetryConstants.TagNames.AuthRoleCount, roleCount);

        // `empty` is a distinct outcome from `resolved`, not a degenerate case of it: the provider
        // answered that this caller holds nothing, which denies every allowlist grant downstream.
        // Reading a 403-storm trace, "the role set was empty" and "the caller had roles but none
        // matched" are different problems and must not look the same.
        activity.SetTag(
            TelemetryConstants.TagNames.AuthOutcome,
            roleCount == 0 ? TelemetryConstants.AuthOutcomes.Empty : TelemetryConstants.AuthOutcomes.Resolved);
        if (roleCount == 0 && emptyReason is not null)
            activity.SetTag(TelemetryConstants.TagNames.AuthEmptyReason, emptyReason);
    }

    /// <summary>
    /// Records that no provider call was made because the caller carried no identity to ask about.
    /// Not an error: anonymous and device tokens are ordinary traffic.
    /// </summary>
    public static void SetSkipped(Activity? activity, bool memoHit)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.AuthOutcome, TelemetryConstants.AuthOutcomes.Skipped);
        activity.SetTag(TelemetryConstants.TagNames.AuthMemoHit, memoHit);
        activity.SetTag(TelemetryConstants.TagNames.AuthRoleCount, 0);
    }

    /// <summary>
    /// Records a failed resolution. The request continues on an empty role set, so the span carries
    /// Error status and the failure kind — without them a provider outage reads, in the trace, exactly
    /// like a caller who genuinely holds nothing.
    /// </summary>
    public static void SetFailed(Activity? activity, string reason, string failureKind, int? statusCode = null)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.AuthOutcome, TelemetryConstants.AuthOutcomes.Failed);
        activity.SetTag(TelemetryConstants.TagNames.AuthFailureKind, failureKind);
        activity.SetTag(TelemetryConstants.TagNames.AuthMemoHit, false);
        activity.SetTag(TelemetryConstants.TagNames.AuthRoleCount, 0);
        if (statusCode.HasValue)
            activity.SetTag(TelemetryConstants.TagNames.AuthProviderStatusCode, statusCode.Value);

        activity.SetStatus(ActivityStatusCode.Error, reason);
    }

    /// <summary>
    /// Records a memo hit whose underlying resolution FAILED. The failure happened on the first
    /// surface's call and is memoized like any other answer; every later surface carries the same
    /// kind and Error status, or only the first span would show the cause.
    /// </summary>
    public static void SetFailedFromMemo(Activity? activity, string failureKind, int? statusCode = null)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.AuthOutcome, TelemetryConstants.AuthOutcomes.Failed);
        activity.SetTag(TelemetryConstants.TagNames.AuthFailureKind, failureKind);
        activity.SetTag(TelemetryConstants.TagNames.AuthMemoHit, true);
        activity.SetTag(TelemetryConstants.TagNames.AuthRoleCount, 0);
        if (statusCode.HasValue)
            activity.SetTag(TelemetryConstants.TagNames.AuthProviderStatusCode, statusCode.Value);
        activity.SetStatus(ActivityStatusCode.Error, "caller roles unresolved (memoized failure); evaluated as empty");
    }

    /// <summary>Operation name for the authorization decision itself.</summary>
    public const string OperationDecide = "Auth.Decide";

    /// <summary>
    /// Starts the span covering one authorization decision.
    /// <para>
    /// The decision — the single bit this endpoint exists to produce — was nowhere in the trace.
    /// Role resolution had a span, the subflow forward had a span, and the answer had neither, so a
    /// 403 could be seen arriving and not explained. This carries the verdict and, when denied, a
    /// bounded reason code.
    /// </para>
    /// <para>
    /// It deliberately carries NO grant expression, role name or caller identity beyond the
    /// <c>sub</c>/<c>act.sub</c> the platform already propagates: a span is exported to a system with
    /// a different access boundary than the workflow's, so an authorization span that repeated the
    /// grants would move an access-control decision's inputs into telemetry.
    /// </para>
    /// </summary>
    public static Activity? StartDecide()
    {
        var activity = ActivitySource.StartActivity(OperationDecide, ActivityKind.Internal);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        return activity;
    }

    /// <summary>Records the verdict and how many roles it was evaluated against.</summary>
    public static void SetDecision(Activity? activity, bool allowed, int roleCount)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.AuthDecision, allowed);
        activity.SetTag(TelemetryConstants.TagNames.AuthRoleCount, roleCount);
    }

    /// <summary>Operation name for the conditional previous-manual-transition lookup.</summary>
    public const string OperationPreviousUserLookup = "Auth.PreviousUserLookup";

    /// <summary>
    /// Starts the span for the conditional <c>$PreviousUser</c> lookup.
    /// <para>
    /// Called only from inside the branch that performs the query, so its presence in a trace means
    /// the extra read happened. It is the one database round trip an authorization evaluation can
    /// add, and it fires only when some grant in the batch actually references the previous user —
    /// which is exactly what makes "was this read paying for a $PreviousUser grant?" unanswerable
    /// without it.
    /// </para>
    /// </summary>
    public static Activity? StartPreviousUserLookup()
    {
        var activity = ActivitySource.StartActivity(OperationPreviousUserLookup, ActivityKind.Internal);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        return activity;
    }

    /// <summary>Operation name for a transition role-filtering pass.</summary>
    public const string OperationFilterTransitions = "Auth.FilterTransitions";

    /// <summary>
    /// Starts the span covering one transition role-filtering pass.
    /// <para>
    /// ONE span for the whole pass, with counts as tags — never one per key. The state function
    /// filters the available transitions of an instance, and on the parent-override path it does so
    /// key by key, creating a fresh evaluator each time; a span per key would turn that O(N) latency
    /// problem into an O(N) telemetry problem and bury it in the very trace meant to reveal it.
    /// Building an evaluator serializes the instance's full latest data, so the creation count is
    /// the number that matters: one is healthy, a number tracking the key count is the defect.
    /// </para>
    /// </summary>
    public static Activity? StartFilterTransitions()
    {
        var activity = ActivitySource.StartActivity(OperationFilterTransitions, ActivityKind.Internal);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        return activity;
    }

    /// <summary>Records the shape of a filtering pass: how many keys, how many survived, how many evaluators it cost.</summary>
    public static void SetFilterResult(Activity? activity, int evaluated, int allowed, int evaluatorCreations)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.AuthKeysEvaluated, evaluated);
        activity.SetTag(TelemetryConstants.TagNames.AuthKeysAllowed, allowed);
        activity.SetTag(TelemetryConstants.TagNames.AuthEvaluatorCreations, evaluatorCreations);
    }
}
