using System;
using System.Collections.Generic;
using System.Linq;
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
/// The rules about <c>morph-idm</c> and the <c>role</c> header. Rule 1 was settled on 2026-09-22;
/// rule 2 replaced the original "never merge" rule on 2026-09-25 by committee decision.
/// </summary>
/// <remarks>
/// <para><b>1. The header is never sent.</b> The endpoint has two modes: asked WITHOUT a role it
/// answers the caller's whole operation set, asked WITH one it switches to a yes/no check for that
/// single role. The runtime needs the set — the grant engine evaluates <c>transition.roles</c>,
/// <c>availableIn</c> narrowing, <c>queryRoles</c> and schema <c>x-roles</c> against it — so sending
/// the header would silently reduce every answer to one role, with no error and no log.</para>
/// <para><b>2. A <c>role</c> header takes precedence, and then morph-idm is not called.</b> When the
/// request carries one, those roles are the caller's set; only a request without one is resolved
/// through the identity service. There is no merge in either direction: the header replaces the
/// service's answer, and without the header the service's answer — or an empty set when it cannot
/// give one — stands alone.</para>
/// </remarks>
[Collection(MorphIdmCallerRoleResolverTests.SpanCollection)]
public sealed class MorphIdmCallerRoleResolverContractTests
{
    private const string Subject = "u-1";
    private const string Actor = "a-1";

    /// <summary>Captures the outbound request so the test can assert what was (not) sent.</summary>
    private sealed class CapturingHandler(HttpStatusCode status, string? body) : HttpMessageHandler
    {
        public HttpRequestMessage? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured = request;
            var response = new HttpResponseMessage(status);
            if (body is not null)
                response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    private static (MorphIdmCallerRoleResolver Resolver, CapturingHandler Handler) Build(
        HttpStatusCode status, string? body)
    {
        var handler = new CapturingHandler(status, body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://morph-idm.test") };

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserName.Returns(Subject);
        currentUser.ActorUserName.Returns(Actor);

        var options = Options.Create(new CallerRoleProviderOptions
        {
            Provider = CallerRoleProviderOptions.MorphIdmProvider,
            MorphIdm = new MorphIdmOptions { BaseUrl = "http://morph-idm.test", GetRolesPath = "/get-roles" }
        });

        return (new MorphIdmCallerRoleResolver(
            client, currentUser, options, NullLogger<MorphIdmCallerRoleResolver>.Instance), handler);
    }

    /// <summary>
    /// The check-mode trap. A <c>role</c> header must never travel outbound — and with rule 2 a request
    /// that carries one makes no outbound call at all.
    /// </summary>
    [Fact]
    public async Task ARequestWithARoleHeaderMakesNoCall_SoTheHeaderIsNeverSent()
    {
        var (resolver, handler) = Build(HttpStatusCode.OK, """{"roles":["idm.approver"]}""");

        await resolver.ResolveRolesAsync(new Dictionary<string, string?>
        {
            [AetherClaimTypes.Role] = "header.role"
        });

        handler.Captured.ShouldBeNull(
            "a role header is the caller's set; sending it would flip the endpoint into check mode");
    }

    /// <summary>What the resolver DOES send: the caller's identity, so morph-idm can answer for them.</summary>
    [Fact]
    public async Task TheCallersIdentityIsSent()
    {
        var (resolver, handler) = Build(HttpStatusCode.OK, """{"roles":["idm.approver"]}""");

        await resolver.ResolveRolesAsync(null);

        handler.Captured!.Headers.GetValues(AetherClaimTypes.UserName).ShouldContain(Subject);
        handler.Captured!.Headers.GetValues(AetherClaimTypes.ActorSub).ShouldContain(Actor);
    }

    /// <summary>
    /// The header REPLACES the service's answer — no merge. <c>idm.approver</c> is what morph-idm would
    /// have said; it must not appear, because morph-idm was not asked.
    /// </summary>
    [Fact]
    public async Task TheHeaderRoleReplacesTheServicesAnswer()
    {
        var (resolver, _) = Build(HttpStatusCode.OK, """{"roles":["idm.approver"]}""");

        var resolved = await resolver.ResolveRolesAsync(new Dictionary<string, string?>
        {
            [AetherClaimTypes.Role] = "header.role"
        });

        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value.ShouldBe(["header.role"]);
    }

    /// <summary>
    /// Without a header, <c>204</c> ("this caller has no operation set") is an EMPTY set.
    /// </summary>
    [Fact]
    public async Task WithoutAHeader_NoContentYieldsAnEmptySet()
    {
        var (resolver, _) = Build(HttpStatusCode.NoContent, null);

        var resolved = await resolver.ResolveRolesAsync(new Dictionary<string, string?>());

        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value.ShouldNotBeNull();
        resolved.Value!.ShouldBeEmpty();
    }

    /// <summary>
    /// Without a header, a provider that cannot answer resolves to an EMPTY set — the request is not
    /// broken (settled 2026-09-24): allowlist grants cannot match it and a role-bound deny refuses it.
    /// </summary>
    [Fact]
    public async Task WithoutAHeader_AServerErrorIsAnEmptySet()
    {
        var (resolver, _) = Build(HttpStatusCode.InternalServerError, "boom");

        var resolved = await resolver.ResolveRolesAsync(new Dictionary<string, string?>());

        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value.ShouldNotBeNull();
        resolved.Value!.ShouldBeEmpty();
    }

    /// <summary>All three response shapes the parser accepts, so a provider change cannot go unnoticed.</summary>
    [Theory]
    [InlineData("""{"roles":["idm.approver"]}""")]
    [InlineData("""{"data":{"roles":["idm.approver"]}}""")]
    [InlineData("""{"getRoles":{"data":{"roles":["idm.approver"]}}}""")]
    public async Task EveryAcceptedResponseShapeResolves(string body)
    {
        var (resolver, _) = Build(HttpStatusCode.OK, body);

        var resolved = await resolver.ResolveRolesAsync(null);

        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value.ShouldBe(["idm.approver"]);
    }
}
