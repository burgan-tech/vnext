using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Authorization.Configuration;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Authorization;

/// <summary>
/// Unit tests for the morph-idm caller-role provider. The three properties that matter operationally:
/// it sends identity but never a <c>role</c> (which would switch the endpoint into its authorize mode),
/// it fails closed, and it calls the endpoint at most once per scope.
/// </summary>
public sealed class MorphIdmCallerRoleResolverTests
{
    private const string Actor = "41809307440";
    private const string Subject = "def-inc";
    private const string Position = "finance-manager";

    // ── Response shapes ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"roles":["idm.full-authorized","idm.viewer"]}""")]
    [InlineData("""{"data":{"roles":["idm.full-authorized","idm.viewer"]}}""")]
    [InlineData("""{"getRoles":{"data":{"roles":["idm.full-authorized","idm.viewer"]}}}""")]
    public async Task ReadsTheRolesArray_FromEveryKnownEnvelope(string body)
    {
        var (resolver, _) = Build(Respond(HttpStatusCode.OK, body));

        var result = await resolver.ResolveRolesAsync(null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(["idm.full-authorized", "idm.viewer"]);
    }

    /// <summary>
    /// "No operation set" is a real answer, distinct from a failure: the caller is known and holds
    /// nothing. It must surface as an empty set so allowlist grants deny, never as a failure and never
    /// as a fall-through to some other role source.
    /// </summary>
    [Fact]
    public async Task NoContent_IsAnEmptySet_NotAFailure()
    {
        var (resolver, _) = Build(Respond(HttpStatusCode.NoContent, string.Empty));

        var result = await resolver.ResolveRolesAsync(null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
    }

    // ── Fail-closed ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task NonSuccessStatus_Fails(HttpStatusCode status)
    {
        var (resolver, _) = Build(Respond(status, "{}"));

        var result = await resolver.ResolveRolesAsync(null);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.CallerRoleResolutionFailed);
    }

    [Fact]
    public async Task TransportException_Fails()
    {
        var (resolver, _) = Build(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
                throw new HttpRequestException("connection refused")));

        var result = await resolver.ResolveRolesAsync(null);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.CallerRoleResolutionFailed);
    }

    /// <summary>
    /// An unparseable body says nothing about the caller, so it cannot be read as "no roles" — that
    /// would turn a malformed provider response into a silent, permanent denial-shaped success.
    /// </summary>
    [Fact]
    public async Task UnrecognizedShape_Fails_RatherThanReadingAsEmpty()
    {
        var (resolver, _) = Build(Respond(HttpStatusCode.OK, """{"unexpected":true}"""));

        var result = await resolver.ResolveRolesAsync(null);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.CallerRoleResolutionFailed);
    }

    // ── Request shape ───────────────────────────────────────────────────────────

    /// <summary>
    /// The endpoint has two behaviours keyed on the presence of <c>role</c>: with it, it authorizes a
    /// single role and 403s; without it, it returns the operation set. The runtime needs the set, so
    /// sending <c>role</c> here would quietly change what the whole call means.
    /// </summary>
    [Fact]
    public async Task SendsIdentityHeaders_AndNeverARoleHeader()
    {
        HttpRequestMessage? captured = null;
        var (resolver, _) = Build(request =>
        {
            captured = request;
            return Respond(HttpStatusCode.OK, """{"roles":["a"]}""")(request);
        });

        await resolver.ResolveRolesAsync(null);

        captured.ShouldNotBeNull();
        Header(captured, "act_sub").ShouldBe(Actor);
        Header(captured, "sub").ShouldBe(Subject);
        Header(captured, "position").ShouldBe(Position);
        captured.Headers.Contains("role").ShouldBeFalse();
    }

    /// <summary>
    /// In a background transition scope there is no ambient HTTP request, so nothing has populated
    /// <c>ICurrentUser.Position</c> and the forwarded headers are the only source left.
    /// </summary>
    [Fact]
    public async Task FallsBackToTheHeaderDictionary_ForPosition_WhenNoAmbientRequest()
    {
        HttpRequestMessage? captured = null;
        var (resolver, _) = Build(
            request =>
            {
                captured = request;
                return Respond(HttpStatusCode.OK, """{"roles":["a"]}""")(request);
            },
            position: null);

        await resolver.ResolveRolesAsync(
            new Dictionary<string, string?> { ["position"] = "branch-teller" });

        Header(captured!, "position").ShouldBe("branch-teller");
    }

    // ── Memoization ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One request means one provider call, however many surfaces ask — and they do ask concurrently:
    /// the human-task and subflow reads both fan out inside a single scope.
    /// </summary>
    [Fact]
    public async Task ConcurrentResolves_TriggerExactlyOneProviderCall()
    {
        var gate = new TaskCompletionSource();
        var (resolver, counter) = Build(async request =>
        {
            await gate.Task;
            return Respond(HttpStatusCode.OK, """{"roles":["a"]}""")(request);
        });

        var calls = Enumerable.Range(0, 8).Select(_ => resolver.ResolveRolesAsync(null)).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        counter.Count.ShouldBe(1);
        results.ShouldAllBe(r => r.IsSuccess);
    }

    /// <summary>
    /// Failures are memoized too. Retrying per surface would multiply an outage by the number of
    /// authorization surfaces a single request touches.
    /// </summary>
    [Fact]
    public async Task FailureIsMemoized_NotRetriedPerSurface()
    {
        var (resolver, counter) = Build(Respond(HttpStatusCode.InternalServerError, "{}"));

        for (var i = 0; i < 4; i++)
            (await resolver.ResolveRolesAsync(null)).IsSuccess.ShouldBeFalse();

        counter.Count.ShouldBe(1);
    }

    // ── Tracing ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A span on the miss path, carrying the caller identity the provider was keyed on and the size
    /// of the answer. The outbound GET already produces an HTTP client span; this one is what says
    /// WHO it was for and WHAT came back.
    /// </summary>
    [Fact]
    public async Task AProviderCall_EmitsASpanTaggedWithTheCallerAndTheRoleCount()
    {
        using var spans = new SpanCollector();
        var (resolver, _) = Build(Respond(HttpStatusCode.OK, """{"roles":["a","b"]}"""));

        await resolver.ResolveRolesAsync(null);

        var span = Assert.Single(spans.Captured);
        Assert.Equal(AuthorizationActivityHelper.OperationResolveRoles, span.OperationName);
        Assert.Equal(false, Tag(span, TelemetryConstants.TagNames.AuthMemoHit));
        Assert.Equal(2, Tag(span, TelemetryConstants.TagNames.AuthRoleCount));
        Assert.Equal("resolved", Tag(span, TelemetryConstants.TagNames.AuthOutcome));
        Assert.Equal(Subject, Tag(span, TelemetryConstants.TagNames.Sub));
        Assert.Equal(Actor, Tag(span, TelemetryConstants.TagNames.ActSub));
        Assert.Equal(Position, Tag(span, TelemetryConstants.TagNames.AuthPosition));
    }

    /// <summary>
    /// The reason this instrumentation exists. Six surfaces ask, one call happens — and all six get
    /// a span, five of them tagged as memo hits. Without the hit spans a heavily-memoized request is
    /// indistinguishable in the tree from one that only ever asked once, and the guarantee the memo
    /// exists to provide becomes unverifiable outside the unit test.
    /// </summary>
    [Fact]
    public async Task EverySurface_GetsASpan_ButOnlyOneIsAProviderCall()
    {
        using var spans = new SpanCollector();
        var (resolver, counter) = Build(Respond(HttpStatusCode.OK, """{"roles":["a"]}"""));

        for (var i = 0; i < 6; i++)
            await resolver.ResolveRolesAsync(null);

        counter.Count.ShouldBe(1);
        spans.Captured.Count.ShouldBe(6);
        spans.Captured.Count(s => Equals(Tag(s, TelemetryConstants.TagNames.AuthMemoHit), false)).ShouldBe(1);
        spans.Captured.Count(s => Equals(Tag(s, TelemetryConstants.TagNames.AuthMemoHit), true)).ShouldBe(5);
    }

    /// <summary>
    /// "No operation set" is a resolution, not a failure — the span must not carry Error status, or
    /// a caller who legitimately holds nothing shows up in APM as a provider outage.
    /// </summary>
    [Fact]
    public async Task NoContent_IsTaggedEmpty_AndIsNotAnError()
    {
        using var spans = new SpanCollector();
        var (resolver, _) = Build(Respond(HttpStatusCode.NoContent, string.Empty));

        await resolver.ResolveRolesAsync(null);

        var span = Assert.Single(spans.Captured);
        Assert.Equal("empty", Tag(span, TelemetryConstants.TagNames.AuthOutcome));
        Assert.Equal(0, Tag(span, TelemetryConstants.TagNames.AuthRoleCount));
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    /// <summary>
    /// Fail-closed is invisible in logs alone once a request fans out. The span carries Error status
    /// and the provider's status code, so the 403s downstream have a traceable cause.
    /// </summary>
    [Fact]
    public async Task AFailedCall_MarksTheSpanErrorWithTheProviderStatus()
    {
        using var spans = new SpanCollector();
        var (resolver, _) = Build(Respond(HttpStatusCode.InternalServerError, "{}"));

        await resolver.ResolveRolesAsync(null);

        var span = Assert.Single(spans.Captured);
        Assert.Equal("failed", Tag(span, TelemetryConstants.TagNames.AuthOutcome));
        Assert.Equal(500, Tag(span, TelemetryConstants.TagNames.AuthProviderStatusCode));
        span.Status.ShouldBe(ActivityStatusCode.Error);
    }

    /// <summary>
    /// A memoized FAILURE still denies, so its spans stay Error — otherwise only the first surface's
    /// span shows the cause and the rest look like clean resolutions that happened to return nothing.
    /// </summary>
    [Fact]
    public async Task AMemoizedFailure_KeepsErrorStatusOnEverySurface()
    {
        using var spans = new SpanCollector();
        var (resolver, _) = Build(Respond(HttpStatusCode.BadGateway, "{}"));

        await resolver.ResolveRolesAsync(null);
        await resolver.ResolveRolesAsync(null);

        spans.Captured.Count.ShouldBe(2);
        spans.Captured.ShouldAllBe(s => s.Status == ActivityStatusCode.Error);
        spans.Captured.Count(s => Equals(Tag(s, TelemetryConstants.TagNames.AuthMemoHit), true)).ShouldBe(1);
    }

    private static object? Tag(Activity activity, string name) =>
        activity.GetTagItem(name);

    /// <summary>
    /// Captures the helper's spans. An ActivitySource with no listener returns null from
    /// StartActivity, so without this every tracing assertion would pass vacuously.
    /// </summary>
    private sealed class SpanCollector : IDisposable
    {
        private readonly ActivityListener _listener;

        public List<Activity> Captured { get; } = [];

        public SpanCollector()
        {
            _listener = new ActivityListener
            {
                // Matched against the const, never against AuthorizationActivityHelper.ActivitySource:
                // AddActivityListener runs this predicate while constructing sources, so touching the
                // helper's static field here re-enters its still-running initializer and poisons the
                // type for every later test in the process.
                ShouldListenTo = source => source.Name == AuthorizationActivityHelper.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity => Captured.Add(activity)
            };
            ActivitySource.AddActivityListener(_listener);

            // A collector that is not actually listening turns every tracing assertion into a
            // vacuous pass, so prove the wiring here rather than discovering it as a mystery
            // "collection was empty" in one test and a silent green in the next.
            if (!AuthorizationActivityHelper.ActivitySource.HasListeners())
                throw new InvalidOperationException("SpanCollector registered but the source has no listeners");
        }

        public void Dispose() => _listener.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static Func<HttpRequestMessage, HttpResponseMessage> Respond(
        HttpStatusCode status, string body) =>
        _ => new HttpResponseMessage(status) { Content = new StringContent(body) };

    private static (MorphIdmCallerRoleResolver Resolver, CallCounter Counter) Build(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        string? position = Position) =>
        Build(request => Task.FromResult(handler(request)), position);

    private static (MorphIdmCallerRoleResolver Resolver, CallCounter Counter) Build(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        string? position = Position)
    {
        var counter = new CallCounter();
        var httpClient = new HttpClient(new StubHandler(handler, counter))
        {
            BaseAddress = new Uri("https://idm.test")
        };

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserName.Returns(Subject);
        currentUser.ActorUserName.Returns(Actor);
        currentUser.Position.Returns(position);

        var options = Options.Create(new CallerRoleProviderOptions
        {
            Provider = CallerRoleProviderOptions.MorphIdmProvider
        });

        return (new MorphIdmCallerRoleResolver(
            httpClient,
            currentUser,
            options,
            NullLogger<MorphIdmCallerRoleResolver>.Instance), counter);
    }

    private sealed class CallCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        CallCounter counter) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            counter.Increment();
            return handler(request);
        }
    }
}
