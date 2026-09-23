using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.LongPoll;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// The <c>role</c> request parameter is a caller's own claim, and under a provider that is an
/// authority it must buy nothing.
/// </summary>
/// <remarks>
/// <para><b>The hole this closes.</b> When the provider answers "no roles", <c>authorize</c> used to
/// fall back to the <c>role</c> query parameter unconditionally. Under the default provider that is
/// harmless — its own source is the caller's <c>role</c> header, so the parameter is the same claim
/// through a different door. Under morph-idm it was not: a <c>204</c> means the identity service
/// decided this caller has no operations, and the parameter overrode that decision. Measured on the
/// running lab before the fix, same caller and same instance:</para>
/// <code>
/// ?queryRoles=true                    -> {"allowed":false}  403
/// ?queryRoles=true&amp;role=chain.admin   -> {"allowed":true}   200
/// </code>
/// <para>It is the same hole the "never forward the <c>role</c> header to morph-idm" rule closes,
/// reached through the query string instead — and it matters more since <c>authorize</c> became the
/// only place these questions are answered: a gateway forwarding the client's query string would be
/// admitting on the client's own claim.</para>
/// <para><b>Why the flag sits on the resolver.</b> Reading the provider name from configuration here
/// would put a second definition of "which provider is this" in the Application layer. The resolver
/// already is that seam; a new provider now has to state its own answer rather than inherit one.</para>
/// </remarks>
public sealed class AuthorizeRoleParameterFallbackTests : IDisposable
{
    private const string Domain = "core";
    private const string Flow = "flow";
    private const string ClaimedRole = "caller.claimed";

    private readonly IComponentCacheStore _componentCache = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly ITransitionAuthorizationManager _authManager = Substitute.For<ITransitionAuthorizationManager>();
    private readonly ICallerRoleResolver _roleResolver = Substitute.For<ICallerRoleResolver>();
    private readonly ILongPollInteractionGate _longPollGate = Substitute.For<ILongPollInteractionGate>();
    private readonly AuthorizeAppService _sut;
    private readonly IServiceProvider? _previousAmbient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public AuthorizeRoleParameterFallbackTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var uow = Substitute.For<IUnitOfWork>();
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(uow));
        services.AddSingleton(uowManager);
        var provider = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = provider;

        // The provider answered, and it answered "none" — the 204 case, not a failure.
        _roleResolver.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok([]));

        _sut = new AuthorizeAppService(
            provider,
            Substitute.For<IRuntimeInfoProvider>(),
            _componentCache,
            _instanceRepository,
            _authManager,
            Substitute.For<IAuthorizeGateway>(),
            _roleResolver,
            _longPollGate,
            Substitute.For<ILogger<AuthorizeAppService>>());
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbient;

    // ── the three fallback targets ──────────────────────────────────────────────────────────

    /// <summary>
    /// An authority provider: the parameter must not reach the grant evaluation at all. Asserting on
    /// the ROLE SET the manager was given, rather than on the verdict, is deliberate — a verdict can
    /// be right for the wrong reason, and what is under test is which caller was evaluated.
    /// </summary>
    [Fact]
    public async Task QueryRoles_UnderAnAuthorityProvider_DoesNotEvaluateTheClaimedRole()
    {
        GivenProviderIsAuthority();
        GivenInstance();

        await AuthorizeQueryRolesAsync(ClaimedRole);

        await _authManager.Received(1).IsQueryAllowedAsync(
            Arg.Any<WorkflowDefinition>(),
            Arg.Any<Instance>(),
            Arg.Is<IReadOnlyCollection<string>?>(r => r == null || !r.Contains(ClaimedRole)),
            Arg.Any<AuthorizationRequestContext?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The default provider keeps the fallback: its own source is the caller's <c>role</c> header, so
    /// refusing the parameter here would break probing without closing anything.
    /// </summary>
    [Fact]
    public async Task QueryRoles_UnderTheDefaultProvider_StillEvaluatesTheParameter()
    {
        GivenProviderAllowsFallback();
        GivenInstance();

        await AuthorizeQueryRolesAsync(ClaimedRole);

        await _authManager.Received(1).IsQueryAllowedAsync(
            Arg.Any<WorkflowDefinition>(),
            Arg.Any<Instance>(),
            Arg.Is<IReadOnlyCollection<string>?>(r => r != null && r.Contains(ClaimedRole)),
            Arg.Any<AuthorizationRequestContext?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The end-to-end shape of the measured defect: with a grant set that admits only the claimed
    /// role, an authority provider's "no operations" must stay a refusal.
    /// </summary>
    [Fact]
    public async Task QueryRoles_UnderAnAuthorityProvider_TheClaimedRoleDoesNotBuyAVerdict()
    {
        GivenProviderIsAuthority();
        GivenInstance();
        // Stand in for the real evaluator: allowed only if the claimed role is in the set.
        _authManager.IsQueryAllowedAsync(
                Arg.Any<WorkflowDefinition>(), Arg.Any<Instance>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<AuthorizationRequestContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<string>?>(2) is { } roles && roles.Contains(ClaimedRole));

        var result = await AuthorizeQueryRolesAsync(ClaimedRole);

        result.Value!.Allowed.ShouldBeFalse(
            "morph-idm answered 204 for this caller; naming a role in the query string must not override it");
    }

    /// <summary>The transition target reads the same role set, so it must follow the same rule.</summary>
    [Fact]
    public async Task TransitionKey_UnderAnAuthorityProvider_DoesNotEvaluateTheClaimedRole()
    {
        GivenProviderIsAuthority();
        GivenInstance();

        await _sut.GetAuthorizeResultForInstanceAsync(
            Domain, Flow, Guid.NewGuid().ToString(), role: ClaimedRole,
            transitionKey: "approve", functionKey: null, version: null,
            checkQueryRoles: false, checkAck: false,
            requestContext: Context());

        await _authManager.DidNotReceive().IsTransitionAllowedInStateAsync(
            Arg.Any<WorkflowDefinition>(),
            Arg.Any<Transition>(),
            Arg.Any<string?>(),
            Arg.Any<Instance?>(),
            Arg.Is<IReadOnlyCollection<string>?>(r => r != null && r.Contains(ClaimedRole)),
            Arg.Any<AuthorizationRequestContext?>(),
            Arg.Any<CancellationToken>());
    }

    // ── the additive target ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>ack</c> composes the parameter ADDITIVELY rather than as a fallback, which would have made
    /// it the one target where a caller could still name its own role — the interaction's roles arm
    /// evaluated against a set morph-idm never returned.
    /// </summary>
    [Fact]
    public async Task Ack_UnderAnAuthorityProvider_DoesNotAddTheClaimedRole()
    {
        GivenProviderIsAuthority();
        var instance = GivenInstance();
        instance.ArmLongPollAck(Guid.NewGuid());

        // The gate receives the role set through a factory; capture it on the arrangement, then run
        // it and inspect what it yields.
        Func<CancellationToken, Task<Result<IReadOnlyCollection<string>>>>? factory = null;
        _longPollGate.IsAdmittedAsync(
                Arg.Any<Instance>(), Arg.Any<WorkflowDefinition>(), Arg.Any<State?>(),
                Arg.Any<Dictionary<string, string?>?>(), Arg.Any<Dictionary<string, string?>?>(),
                Arg.Do<Func<CancellationToken, Task<Result<IReadOnlyCollection<string>>>>>(f => factory = f),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(true));

        await _sut.GetAuthorizeResultForInstanceAsync(
            Domain, Flow, Guid.NewGuid().ToString(), role: ClaimedRole,
            transitionKey: null, functionKey: null, version: null,
            checkQueryRoles: false, checkAck: true,
            requestContext: Context());

        factory.ShouldNotBeNull();
        var roles = (await factory!(CancellationToken.None)).Value;
        (roles ?? []).ShouldNotContain(ClaimedRole,
            "under an authority provider the ack pre-flight must evaluate the service's set, not the caller's claim");
    }

    /// <summary>And the default provider keeps the additive behaviour.</summary>
    [Fact]
    public async Task Ack_UnderTheDefaultProvider_StillAddsTheClaimedRole()
    {
        GivenProviderAllowsFallback();
        var instance = GivenInstance();
        instance.ArmLongPollAck(Guid.NewGuid());

        Func<CancellationToken, Task<Result<IReadOnlyCollection<string>>>>? factory = null;
        _longPollGate.IsAdmittedAsync(
                Arg.Any<Instance>(), Arg.Any<WorkflowDefinition>(), Arg.Any<State?>(),
                Arg.Any<Dictionary<string, string?>?>(), Arg.Any<Dictionary<string, string?>?>(),
                Arg.Do<Func<CancellationToken, Task<Result<IReadOnlyCollection<string>>>>>(f => factory = f),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(true));

        await _sut.GetAuthorizeResultForInstanceAsync(
            Domain, Flow, Guid.NewGuid().ToString(), role: ClaimedRole,
            transitionKey: null, functionKey: null, version: null,
            checkQueryRoles: false, checkAck: true,
            requestContext: Context());

        factory.ShouldNotBeNull();
        var roles = (await factory!(CancellationToken.None)).Value;
        roles.ShouldContain(ClaimedRole);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private void GivenProviderIsAuthority() =>
        _roleResolver.AllowsRoleParameterFallback.Returns(false);

    private void GivenProviderAllowsFallback() =>
        _roleResolver.AllowsRoleParameterFallback.Returns(true);

    private static AuthorizationRequestContext Context() =>
        new(new Dictionary<string, string?>());

    private Instance GivenInstance()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "key");
        instance.ChangeState(State.Create(
            "waiting", StateType.Intermediate, StateSubType.None, VersionStrategy.IncreaseMinor.Code));

        _instanceRepository.FindByIdentifierAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(instance);
        _componentCache.GetFlowAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(BuildWorkflow()));
        return instance;
    }

    private Task<Result<AuthorizeOutput>> AuthorizeQueryRolesAsync(string roleParameter) =>
        _sut.GetAuthorizeResultForInstanceAsync(
            Domain, Flow, Guid.NewGuid().ToString(), role: roleParameter,
            transitionKey: null, functionKey: null, version: null,
            checkQueryRoles: true, checkAck: false,
            requestContext: Context());

    private static WorkflowDefinition BuildWorkflow() =>
        JsonSerializer.Deserialize<WorkflowDefinition>("""
            {
              "key": "flow",
              "type": "F",
              "timeout": null,
              "labels": [],
              "functions": [],
              "features": [],
              "states": [
                { "key": "waiting", "stateType": "intermediate", "labels": [],
                  "transitions": [ { "key": "approve", "target": "waiting", "triggerType": "Manual",
                                     "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [] } ] }
              ],
              "sharedTransitions": [],
              "extensions": [],
              "queryRoles": [],
              "startTransition": { "key": "start", "from": null, "target": "waiting",
                                   "triggerType": "Manual", "versionStrategy": "Patch",
                                   "labels": [], "onExecutionTasks": [], "view": null }
            }
            """, JsonOptions)!;
}
