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
    public const string SourceName = "BBT.Workflow.Authorization";

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
    public static void SetResolved(Activity? activity, int roleCount, bool memoHit)
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
    }

    /// <summary>
    /// Records a failed resolution. Fail-closed means every surface in this request is about to
    /// answer 403, so the span carries Error status — this is the one place the cause is visible in
    /// the trace rather than only in the log.
    /// </summary>
    public static void SetFailed(Activity? activity, string reason, int? statusCode = null)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.AuthOutcome, TelemetryConstants.AuthOutcomes.Failed);
        activity.SetTag(TelemetryConstants.TagNames.AuthMemoHit, false);
        if (statusCode.HasValue)
            activity.SetTag(TelemetryConstants.TagNames.AuthProviderStatusCode, statusCode.Value);

        activity.SetStatus(ActivityStatusCode.Error, reason);
    }

    /// <summary>
    /// Records a memo hit whose underlying resolution FAILED. Without this the span would be
    /// unterminated: the outcome is a denial, but nothing failed here — the failure happened on the
    /// first surface's call and is memoized like any other answer.
    /// </summary>
    public static void SetFailedFromMemo(Activity? activity)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.AuthOutcome, TelemetryConstants.AuthOutcomes.Failed);
        activity.SetTag(TelemetryConstants.TagNames.AuthMemoHit, true);
        activity.SetStatus(ActivityStatusCode.Error, "caller roles unresolved (memoized failure)");
    }
}
