using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Authorization.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Authorization;

/// <summary>
/// The morph-idm provider's role-outcome seam (<see cref="ICallerRoleResolver.ResolveRoleSetAsync"/>)
/// and its per-credential memo. Row security tells "resolution failed" from "the caller holds no
/// roles" with the outcome, and a same-domain trigger task that reads under a different credential in
/// the same DI scope must get its own resolution rather than the pipeline caller's.
/// </summary>
[Collection(MorphIdmCallerRoleResolverTests.SpanCollection)]
public sealed class MorphIdmCallerRoleResolverOutcomeTests
{
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public MorphIdmCallerRoleResolverOutcomeTests()
    {
        _user.ActorUserName.Returns("rs-idm-hq");
        _user.UserName.Returns("rs-idm-hq");
        _user.Position.Returns((string?)null);
        _user.Roles.Returns([]);
    }

    [Fact]
    public async Task Memo_IsKeyedPerCredential_SecondCredentialGetsItsOwnCall()
    {
        var handler = new StubHandler(request =>
            Respond(HttpStatusCode.OK, Header(request, "act_sub") == "rs-idm-hq"
                ? """{"roles":["hq-auditor"]}"""
                : """{"roles":["branch-staff"]}"""));
        var resolver = Build(handler);

        (await resolver.ResolveRolesAsync(null)).Value.ShouldBe(["hq-auditor"]);

        _user.ActorUserName.Returns("rs-idm-branch");
        _user.UserName.Returns("rs-idm-branch");
        (await resolver.ResolveRolesAsync(null)).Value.ShouldBe(["branch-staff"]);

        // Back to the first credential: served from its own memo entry.
        _user.ActorUserName.Returns("rs-idm-hq");
        _user.UserName.Returns("rs-idm-hq");
        (await resolver.ResolveRolesAsync(null)).Value.ShouldBe(["hq-auditor"]);

        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task SameCredential_TwoSurfaces_OneCall()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """{"roles":["hq-auditor"]}"""));
        var resolver = Build(handler);

        (await resolver.ResolveRolesAsync(null)).Value.ShouldBe(["hq-auditor"]);
        var set = await resolver.ResolveRoleSetAsync(null);

        set.Outcome.ShouldBe(CallerRoleOutcome.Resolved);
        set.Roles.ShouldBe(["hq-auditor"]);
        set.FailureKind.ShouldBeNull();
        handler.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(500, CallerRoleOutcome.Failed)]
    [InlineData(204, CallerRoleOutcome.Empty)]
    public async Task Outcome_FromStatus(int status, CallerRoleOutcome expected)
    {
        var handler = new StubHandler(_ => Respond((HttpStatusCode)status, status == 204 ? "" : "{}"));
        var resolver = Build(handler);

        var set = await resolver.ResolveRoleSetAsync(null);

        set.Outcome.ShouldBe(expected);
        set.Roles.ShouldBeEmpty();
        if (expected == CallerRoleOutcome.Failed)
            set.FailureKind.ShouldBe("http_status");
        else
            set.FailureKind.ShouldBeNull();
    }

    [Fact]
    public async Task EmptyRolesArray_IsEmpty()
    {
        var resolver = Build(new StubHandler(_ => Respond(HttpStatusCode.OK, """{"roles":[]}""")));

        (await resolver.ResolveRoleSetAsync(null)).Outcome.ShouldBe(CallerRoleOutcome.Empty);
    }

    [Fact]
    public async Task UnparseableBody_IsFailed()
    {
        var resolver = Build(new StubHandler(_ => Respond(HttpStatusCode.OK, "not json")));

        var set = await resolver.ResolveRoleSetAsync(null);

        set.Outcome.ShouldBe(CallerRoleOutcome.Failed);
        set.FailureKind.ShouldBe("parse");
        set.Roles.ShouldBeEmpty();
    }

    [Fact]
    public async Task TransportFailure_IsFailed()
    {
        var resolver = Build(new StubHandler(_ =>
            throw new HttpRequestException("connection refused to secret-host:443")));

        var set = await resolver.ResolveRoleSetAsync(null);

        set.Outcome.ShouldBe(CallerRoleOutcome.Failed);
        // A coarse kind only — never the exception text.
        set.FailureKind.ShouldBe("transport");
        set.Roles.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoActorNoClientId_IsSkipped()
    {
        _user.ActorUserName.Returns((string?)null);
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """{"roles":["a"]}"""));
        var resolver = Build(handler);

        var set = await resolver.ResolveRoleSetAsync(null);

        set.Outcome.ShouldBe(CallerRoleOutcome.Skipped);
        set.Roles.ShouldBeEmpty();
        set.FailureKind.ShouldBeNull();
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task RoleHeader_IsFromHeader_NoCall()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """{"roles":["idm.viewer"]}"""));
        var resolver = Build(handler);

        var set = await resolver.ResolveRoleSetAsync(
            new Dictionary<string, string?> { ["role"] = "header.approver,header.maker" });

        set.Outcome.ShouldBe(CallerRoleOutcome.FromHeader);
        set.Roles.ShouldBe(["header.approver", "header.maker"]);
        handler.Calls.ShouldBe(0);
    }

    /// <summary>
    /// A different <c>role</c> header in the same scope is a different credential: it is answered
    /// from that header, not from the earlier caller's memoized set.
    /// </summary>
    [Fact]
    public async Task DifferentRoleHeader_SameScope_IsItsOwnResolution()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """{"roles":["idm.viewer"]}"""));
        var resolver = Build(handler);

        (await resolver.ResolveRolesAsync(new Dictionary<string, string?> { ["role"] = "a" })).Value.ShouldBe(["a"]);
        (await resolver.ResolveRolesAsync(new Dictionary<string, string?> { ["role"] = "b" })).Value.ShouldBe(["b"]);

        handler.Calls.ShouldBe(0);
    }

    /// <summary>
    /// A surface that passes no headers borrows only headers captured under the SAME ambient identity.
    /// Headers carrying a <c>role</c> that one caller passed must not answer a null-header surface after
    /// the scope switched to another credential (<c>ICurrentUser.Change</c>, a trigger task).
    /// </summary>
    [Fact]
    public async Task NullHeaders_AfterIdentitySwitch_DoNotBorrowAnotherIdentitysRoleHeader()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """{"roles":["idm.viewer"]}"""));
        var resolver = Build(handler);

        (await resolver.ResolveRoleSetAsync(new Dictionary<string, string?> { ["role"] = "task.approver" }))
            .Outcome.ShouldBe(CallerRoleOutcome.FromHeader);

        _user.ActorUserName.Returns("rs-idm-branch");
        _user.UserName.Returns("rs-idm-branch");
        var set = await resolver.ResolveRoleSetAsync(null);

        set.Outcome.ShouldNotBe(CallerRoleOutcome.FromHeader);
        set.Roles.ShouldBe(["idm.viewer"]);
        handler.Calls.ShouldBe(1);
    }

    /// <summary>The same identity with null headers still lands on the headers it passed earlier.</summary>
    [Fact]
    public async Task NullHeaders_SameIdentity_BorrowCapturedHeaders()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """{"roles":["idm.viewer"]}"""));
        var resolver = Build(handler);

        await resolver.ResolveRoleSetAsync(new Dictionary<string, string?> { ["role"] = "task.approver" });
        var set = await resolver.ResolveRoleSetAsync(null);

        set.Outcome.ShouldBe(CallerRoleOutcome.FromHeader);
        set.Roles.ShouldBe(["task.approver"]);
        handler.Calls.ShouldBe(0);
    }

    /// <summary>
    /// Header roles come back exactly in header order: <c>SingleRoleOf</c> takes the first one, so reordering
    /// would change the caller's single role and disagree with the default provider.
    /// </summary>
    [Fact]
    public async Task RoleHeader_KeepsHeaderOrder()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, """{"roles":["idm.viewer"]}"""));
        var resolver = Build(handler);

        var set = await resolver.ResolveRoleSetAsync(new Dictionary<string, string?> { ["role"] = "b, a" });
        var roles = (await resolver.ResolveRolesAsync(new Dictionary<string, string?> { ["role"] = "b, a" })).Value;

        set.Outcome.ShouldBe(CallerRoleOutcome.FromHeader);
        set.Roles.ShouldBe(["b", "a"]);
        roles.ShouldBe(["b", "a"]);
        ICallerRoleResolver.SingleRoleOf(roles).ShouldBe("b");
        handler.Calls.ShouldBe(0);
    }

    /// <summary>A header repeating a role keeps the caller's list as sent (trimmed, blanks dropped only).</summary>
    [Fact]
    public async Task RoleHeader_WithDuplicate_KeepsCallersList()
    {
        var resolver = Build(new StubHandler(_ => Respond(HttpStatusCode.OK, """{"roles":["idm.viewer"]}""")));

        var set = await resolver.ResolveRoleSetAsync(new Dictionary<string, string?> { ["role"] = "a, b, ,a" });

        set.Outcome.ShouldBe(CallerRoleOutcome.FromHeader);
        set.Roles.ShouldBe(["a", "b", "a"]);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    // Typed as the interface on purpose: callers reach ResolveRoleSetAsync through ICallerRoleResolver,
    // so the override (not the fail-closed default member) must be what that dispatch lands on.
    private ICallerRoleResolver Build(StubHandler handler) =>
        new MorphIdmCallerRoleResolver(
            new HttpClient(handler) { BaseAddress = new Uri("https://idm.test") },
            _user,
            Options.Create(new CallerRoleProviderOptions { Provider = CallerRoleProviderOptions.MorphIdmProvider }),
            NullLogger<MorphIdmCallerRoleResolver>.Instance);

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(respond(request));
        }
    }
}
