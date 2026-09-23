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
/// The two rules the requester settled on 2026-09-22 about <c>morph-idm</c> and the <c>role</c> header.
/// Both are already how the resolver behaves; these tests exist so the behaviour is not "simplified"
/// back later by someone who sees a header going unused and assumes it was an oversight.
/// </summary>
/// <remarks>
/// <para><b>1. The header is never sent.</b> The endpoint has two modes: asked WITHOUT a role it
/// answers the caller's whole operation set, asked WITH one it switches to a yes/no check for that
/// single role. The runtime needs the set — the grant engine evaluates <c>transition.roles</c>,
/// <c>availableIn</c> narrowing, <c>queryRoles</c> and schema <c>x-roles</c> against it — so sending
/// the header would silently reduce every answer to one role, with no error and no log.</para>
/// <para><b>2. The response is never merged with the header.</b> Under this provider only the roles
/// the service returns are valid. Merging would let a gateway-asserted header widen what the identity
/// service governs, and it would quietly undo the <c>204</c> contract below.</para>
/// </remarks>
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
    /// The check-mode trap. A <c>role</c> header on the caller's request must not travel outbound.
    /// </summary>
    [Fact]
    public async Task TheRoleHeaderIsNeverSentToMorphIdm()
    {
        var (resolver, handler) = Build(HttpStatusCode.OK, """{"roles":["idm.approver"]}""");

        await resolver.ResolveRolesAsync(new Dictionary<string, string?>
        {
            [AetherClaimTypes.Role] = "header.role"
        });

        handler.Captured.ShouldNotBeNull();
        handler.Captured!.Headers.Contains(AetherClaimTypes.Role).ShouldBeFalse(
            "sending the role header flips the endpoint into check mode and reduces the answer to a " +
            "yes/no about that one role — a failure with no error and no log");
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
    /// Only the service's roles are valid. A header role present on the request must not appear in
    /// the resolved set.
    /// </summary>
    [Fact]
    public async Task TheResponseIsNotMergedWithTheHeaderRole()
    {
        var (resolver, _) = Build(HttpStatusCode.OK, """{"roles":["idm.approver"]}""");

        var resolved = await resolver.ResolveRolesAsync(new Dictionary<string, string?>
        {
            [AetherClaimTypes.Role] = "header.role"
        });

        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value.ShouldBe(["idm.approver"]);
        resolved.Value!.ShouldNotContain("header.role");
    }

    /// <summary>
    /// <c>204</c> means "this caller has no operation set" — an EMPTY set, not a reason to fall back
    /// to the header. A merge would have quietly undone exactly this.
    /// </summary>
    [Fact]
    public async Task NoContentYieldsAnEmptySetRatherThanAHeaderFallback()
    {
        var (resolver, _) = Build(HttpStatusCode.NoContent, null);

        var resolved = await resolver.ResolveRolesAsync(new Dictionary<string, string?>
        {
            [AetherClaimTypes.Role] = "header.role"
        });

        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value.ShouldNotBeNull();
        resolved.Value!.ShouldBeEmpty();
    }

    /// <summary>
    /// A provider that cannot answer is a resolution FAILURE, not an empty role set — the caller's
    /// authority is unknown, and the only safe reading of unknown is denial (403
    /// <c>CallerRoleResolutionFailed</c>), never "no roles, carry on".
    /// </summary>
    [Fact]
    public async Task AServerErrorIsAFailureNotAnEmptySet()
    {
        var (resolver, _) = Build(HttpStatusCode.InternalServerError, "boom");

        var resolved = await resolver.ResolveRolesAsync(null);

        resolved.IsSuccess.ShouldBeFalse();
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
