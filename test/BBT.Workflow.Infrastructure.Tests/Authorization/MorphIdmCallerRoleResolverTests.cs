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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Authorization;

/// <summary>
/// Unit tests for the morph-idm caller-role provider. The three properties that matter operationally:
/// it sends identity but never a <c>role</c> (which would switch the endpoint into its authorize mode),
/// every failure resolves to an empty role set — logged and tagged by kind, never breaking the
/// request — and it calls the endpoint at most once per scope.
/// </summary>
[Collection(SpanCollection)]
public sealed class MorphIdmCallerRoleResolverTests
{
    /// <summary>
    /// Serializes every test class that makes the resolver emit spans. <see cref="SpanCollector"/>
    /// listens process-wide, so a class running in parallel leaks its spans into another class's
    /// <c>Assert.Single</c>.
    /// </summary>
    public const string SpanCollection = "MorphIdm resolver spans";

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
    /// "No operation set" is a real answer: the caller is known and holds nothing. It must surface as
    /// an empty set, never as a fall-through to some other role source.
    /// </summary>
    [Fact]
    public async Task NoContent_IsAnEmptySet_NotAFailure()
    {
        var (resolver, _) = Build(Respond(HttpStatusCode.NoContent, string.Empty));

        var result = await resolver.ResolveRolesAsync(null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
    }

    // ── Every failure resolves to an empty set ──────────────────────────────────
    //
    // The provider's failure never breaks the request. It resolves to an empty role set and the
    // grant engine decides on that: an allowlist grant cannot match, and a role-bound deny refuses a
    // role-less caller (TransitionAuthorizationManager.IsUnprovableRoleBoundDeny), so an outage
    // narrows what a caller sees and never widens it. What distinguishes the cases is the log level
    // and the span tags, which is what these tests pin.

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.BadGateway, 502)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    public async Task NonSuccessStatus_ResolvesEmpty_AndLogsAnError(HttpStatusCode status, int code)
    {
        using var spans = new SpanCollector();
        var (resolver, _, logger) = BuildWithLogger(Respond(status, "{}"));

        var result = await resolver.ResolveRolesAsync(null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
        logger.Single(LogLevel.Error).EventId.ShouldBe(20442);
        var span = Assert.Single(spans.Captured);
        Assert.Equal("failed", Tag(span, TelemetryConstants.TagNames.AuthOutcome));
        Assert.Equal("http_status", Tag(span, TelemetryConstants.TagNames.AuthFailureKind));
        Assert.Equal(code, Tag(span, TelemetryConstants.TagNames.AuthProviderStatusCode));
        span.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task TransportException_ResolvesEmpty_TaggedTransport()
    {
        using var spans = new SpanCollector();
        var (resolver, _, logger) = BuildWithLogger(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
                throw new HttpRequestException("connection refused")));

        var result = await resolver.ResolveRolesAsync(null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
        logger.Single(LogLevel.Error).EventId.ShouldBe(20442);
        Assert.Equal("transport", Tag(Assert.Single(spans.Captured), TelemetryConstants.TagNames.AuthFailureKind));
    }

    /// <summary>
    /// <c>HttpClient.Timeout</c> surfaces as a <see cref="TaskCanceledException"/>; it is the most
    /// likely failure under load and must be told apart from a refused connection.
    /// </summary>
    [Fact]
    public async Task Timeout_ResolvesEmpty_TaggedTimeout()
    {
        using var spans = new SpanCollector();
        var (resolver, _, logger) = BuildWithLogger(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")));

        var result = await resolver.ResolveRolesAsync(null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
        logger.Single(LogLevel.Error).EventId.ShouldBe(20442);
        Assert.Equal("timeout", Tag(Assert.Single(spans.Captured), TelemetryConstants.TagNames.AuthFailureKind));
    }

    /// <summary>
    /// An unparseable body is a provider defect, not an answer about the caller — logged as its own
    /// error so it is not lost among outages, and still resolved to an empty set.
    /// </summary>
    [Theory]
    [InlineData("""{"unexpected":true}""")]
    [InlineData("""not json""")]
    [InlineData("""["a","b"]""")]
    public async Task UnparseableBody_ResolvesEmpty_WithItsOwnErrorLog(string body)
    {
        using var spans = new SpanCollector();
        var (resolver, _, logger) = BuildWithLogger(Respond(HttpStatusCode.OK, body));

        var result = await resolver.ResolveRolesAsync(null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
        var error = logger.Single(LogLevel.Error);
        error.EventId.ShouldNotBe(20442);
        error.Message.ShouldContain("empty role set");
        var span = Assert.Single(spans.Captured);
        Assert.Equal("failed", Tag(span, TelemetryConstants.TagNames.AuthOutcome));
        Assert.Equal("parse", Tag(span, TelemetryConstants.TagNames.AuthFailureKind));
        span.Status.ShouldBe(ActivityStatusCode.Error);
    }

    /// <summary>
    /// The three shapes of "this caller holds nothing" are one outcome — a Warning, never an Error,
    /// and a span without Error status — told apart by the <c>empty_reason</c> tag. The empty array
    /// used to log only at Debug, which made it invisible next to the 204 it means the same as.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NoContent, "", "no_content")]
    [InlineData(HttpStatusCode.OK, "   ", "empty_body")]
    [InlineData(HttpStatusCode.OK, """{"roles":[]}""", "empty_array")]
    [InlineData(HttpStatusCode.OK, """{"data":{"roles":[" "]}}""", "empty_array")]
    public async Task EmptyAnswers_ResolveEmpty_AndLogAWarning(HttpStatusCode status, string body, string reason)
    {
        using var spans = new SpanCollector();
        var (resolver, _, logger) = BuildWithLogger(Respond(status, body));

        var result = await resolver.ResolveRolesAsync(null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Error);
        logger.Single(LogLevel.Warning).Message.ShouldContain("empty role set");
        var span = Assert.Single(spans.Captured);
        Assert.Equal("empty", Tag(span, TelemetryConstants.TagNames.AuthOutcome));
        Assert.Equal(reason, Tag(span, TelemetryConstants.TagNames.AuthEmptyReason));
        Assert.Null(Tag(span, TelemetryConstants.TagNames.AuthFailureKind));
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    // ── No identity, no call ────────────────────────────────────────────────────

    /// <summary>
    /// With neither <c>act_sub</c> nor <c>client_id</c> there is nobody for morph-idm to answer about
    /// (an anonymous or device token). The call is skipped, the set is empty, and — because this is
    /// ordinary traffic rather than a defect — it logs at Debug only.
    /// </summary>
    [Fact]
    public async Task NoActorAndNoClientId_SkipsTheCall_AndResolvesEmpty()
    {
        using var spans = new SpanCollector();
        var (resolver, counter, logger) = BuildWithLogger(
            Respond(HttpStatusCode.OK, """{"roles":["a"]}"""), actor: null);

        var result = await resolver.ResolveRolesAsync(new Dictionary<string, string?> { ["sub"] = Subject });

        counter.Count.ShouldBe(0);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
        logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Debug);
        var span = Assert.Single(spans.Captured);
        Assert.Equal("skipped", Tag(span, TelemetryConstants.TagNames.AuthOutcome));
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task AClientIdAlone_IsEnoughToCall()
    {
        HttpRequestMessage? captured = null;
        var (resolver, counter, _) = BuildWithLogger(request =>
        {
            captured = request;
            return Respond(HttpStatusCode.OK, """{"roles":["device.reader"]}""")(request);
        }, actor: null);

        var result = await resolver.ResolveRolesAsync(new Dictionary<string, string?> { ["client_id"] = "mobile-app" });

        counter.Count.ShouldBe(1);
        result.Value.ShouldBe(["device.reader"]);
        Header(captured!, "client_id").ShouldBe("mobile-app");
    }

    [Fact]
    public async Task AnActorAlone_IsEnoughToCall()
    {
        var (resolver, counter, _) = BuildWithLogger(Respond(HttpStatusCode.OK, """{"roles":["a"]}"""));

        await resolver.ResolveRolesAsync(null);

        counter.Count.ShouldBe(1);
    }

    /// <summary>
    /// The provider is an authority: even when its failure resolved to an empty set, a caller-named
    /// <c>role</c> parameter must not fill the gap.
    /// </summary>
    [Fact]
    public void NeverAllowsTheRoleParameterFallback()
    {
        var (resolver, _, _) = BuildWithLogger(Respond(HttpStatusCode.InternalServerError, "{}"));
        resolver.AllowsRoleParameterFallback.ShouldBeFalse();
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
        {
            var result = await resolver.ResolveRolesAsync(null);
            result.IsSuccess.ShouldBeTrue();
            result.Value.ShouldBeEmpty();
        }

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
    /// A failure resolved to an empty set is invisible in logs alone once a request fans out. The span
    /// carries Error status and the provider's status code, so a narrowed answer downstream has a
    /// traceable cause.
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
    /// A memoized FAILURE keeps its Error status and failure kind on every surface — otherwise only
    /// the first surface's span shows the cause and the rest look like callers who genuinely hold
    /// nothing.
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
        spans.Captured.ShouldAllBe(s => Equals(Tag(s, TelemetryConstants.TagNames.AuthFailureKind), "http_status"));
        spans.Captured.Count(s => Equals(Tag(s, TelemetryConstants.TagNames.AuthMemoHit), true)).ShouldBe(1);
    }

    [Fact]
    public async Task AMemoizedEmptyAnswer_KeepsItsReasonOnEverySurface()
    {
        using var spans = new SpanCollector();
        var (resolver, _) = Build(Respond(HttpStatusCode.NoContent, string.Empty));

        await resolver.ResolveRolesAsync(null);
        await resolver.ResolveRolesAsync(null);

        spans.Captured.ShouldAllBe(s => Equals(Tag(s, TelemetryConstants.TagNames.AuthOutcome), "empty"));
        spans.Captured.ShouldAllBe(s => Equals(Tag(s, TelemetryConstants.TagNames.AuthEmptyReason), "no_content"));
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
        var (resolver, counter, _) = BuildWithLogger(handler, position);
        return (resolver, counter);
    }

    private static (MorphIdmCallerRoleResolver Resolver, CallCounter Counter, CapturingLogger Logger) BuildWithLogger(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        string? position = Position,
        string? actor = Actor) =>
        BuildWithLogger(request => Task.FromResult(handler(request)), position, actor);

    private static (MorphIdmCallerRoleResolver Resolver, CallCounter Counter, CapturingLogger Logger) BuildWithLogger(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        string? position = Position,
        string? actor = Actor)
    {
        var counter = new CallCounter();
        var httpClient = new HttpClient(new StubHandler(handler, counter))
        {
            BaseAddress = new Uri("https://idm.test")
        };

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserName.Returns(Subject);
        currentUser.ActorUserName.Returns(actor);
        currentUser.Position.Returns(position);

        var options = Options.Create(new CallerRoleProviderOptions
        {
            Provider = CallerRoleProviderOptions.MorphIdmProvider
        });

        var logger = new CapturingLogger();
        return (new MorphIdmCallerRoleResolver(
            httpClient,
            currentUser,
            options,
            logger), counter, logger);
    }

    private sealed record LogEntry(LogLevel Level, int EventId, string Message);

    private sealed class CapturingLogger : ILogger<MorphIdmCallerRoleResolver>
    {
        public List<LogEntry> Entries { get; } = [];

        public LogEntry Single(LogLevel level) => Entries.Single(e => e.Level == level);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
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
