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
/// How the <c>role</c> request parameter of <c>authorize</c> composes with the caller's role set, per
/// provider (<see cref="ICallerRoleResolver.RoleParameterMode"/>).
/// </summary>
/// <remarks>
/// <para><b>Fallback</b> (the default provider): the parameter stands in only when the provider
/// resolved no roles — its own source is the caller's <c>role</c> header, so the parameter is the
/// same claim through a different door. <c>ack</c> adds it on every path.</para>
/// <para><b>AsRoleHeader</b> (morph-idm, 2026-09-25): the parameter is handed to the resolver as the
/// request's <c>role</c> header when the request carries none, so it behaves exactly like one — it is
/// the role set and morph-idm is not asked. A real header wins over it. This replaced the 2026-09-23
/// rule that morph-idm ignored the parameter, once a request's <c>role</c> header was made decisive
/// under that provider: the same claim must not mean different things on the two channels.</para>
/// <para><b>Why the mode sits on the resolver.</b> Reading the provider name from configuration here
/// would put a second definition of "which provider is this" in the Application layer.</para>
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

    // ── morph-idm: the parameter behaves like a `role` header (2026-09-25) ──────────────────
    //
    // Under RoleParameterMode.AsRoleHeader the parameter is handed to the resolver AS the request's
    // `role` header when the request carries none, so the resolver's own header precedence applies:
    // the parameter IS the role set and the identity service is not asked. A real header wins.

    [Fact]
    public async Task QueryRoles_AsRoleHeader_HandsTheParameterToTheResolverAsTheRoleHeader()
    {
        GivenProviderTreatsTheParameterAsTheRoleHeader();
        GivenInstance();

        await AuthorizeQueryRolesAsync(ClaimedRole);

        await _roleResolver.Received().ResolveRolesAsync(
            Arg.Is<IReadOnlyDictionary<string, string?>?>(h => h != null && h["role"] == ClaimedRole),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The resolved set is used as-is — no fallback, no second composition on top.</summary>
    [Fact]
    public async Task QueryRoles_AsRoleHeader_EvaluatesExactlyWhatTheResolverReturned()
    {
        GivenProviderTreatsTheParameterAsTheRoleHeader();
        _roleResolver.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Result<string[]?>.Ok(
                ci.ArgAt<IReadOnlyDictionary<string, string?>?>(0) is { } h && h.TryGetValue("role", out var r) && r != null
                    ? [r] : []));
        GivenInstance();

        await AuthorizeQueryRolesAsync(ClaimedRole);

        await _authManager.Received(1).IsQueryAllowedAsync(
            Arg.Any<WorkflowDefinition>(),
            Arg.Any<Instance>(),
            Arg.Is<IReadOnlyCollection<string>?>(r => r != null && r.Count == 1 && r.Contains(ClaimedRole)),
            Arg.Any<AuthorizationRequestContext?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A real `role` header wins over the parameter — the same order the default provider uses.</summary>
    [Fact]
    public async Task QueryRoles_AsRoleHeader_ARealRoleHeaderWinsOverTheParameter()
    {
        GivenProviderTreatsTheParameterAsTheRoleHeader();
        GivenInstance();

        await _sut.GetAuthorizeResultForInstanceAsync(
            Domain, Flow, Guid.NewGuid().ToString(), role: ClaimedRole,
            transitionKey: null, functionKey: null, version: null,
            checkQueryRoles: true, checkAck: false,
            requestContext: new AuthorizationRequestContext(new Dictionary<string, string?> { ["role"] = "header.role" }));

        await _roleResolver.Received().ResolveRolesAsync(
            Arg.Is<IReadOnlyDictionary<string, string?>?>(h => h != null && h["role"] == "header.role"),
            Arg.Any<CancellationToken>());
        await _roleResolver.DidNotReceive().ResolveRolesAsync(
            Arg.Is<IReadOnlyDictionary<string, string?>?>(h => h != null && h["role"] == ClaimedRole),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryRoles_AsRoleHeader_WithoutAParameterPassesTheRequestHeadersUnchanged()
    {
        GivenProviderTreatsTheParameterAsTheRoleHeader();
        GivenInstance();

        await AuthorizeQueryRolesAsync(roleParameter: "");

        await _roleResolver.Received().ResolveRolesAsync(
            Arg.Is<IReadOnlyDictionary<string, string?>?>(h => h == null || !h.ContainsKey("role")),
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

    /// <summary>The transition target reads the same role set, so it must follow the same rule.</summary>
    [Fact]
    public async Task TransitionKey_AsRoleHeader_HandsTheParameterToTheResolverAsTheRoleHeader()
    {
        GivenProviderTreatsTheParameterAsTheRoleHeader();
        GivenInstance();

        await _sut.GetAuthorizeResultForInstanceAsync(
            Domain, Flow, Guid.NewGuid().ToString(), role: ClaimedRole,
            transitionKey: "approve", functionKey: null, version: null,
            checkQueryRoles: false, checkAck: false,
            requestContext: Context());

        await _roleResolver.Received().ResolveRolesAsync(
            Arg.Is<IReadOnlyDictionary<string, string?>?>(h => h != null && h["role"] == ClaimedRole),
            Arg.Any<CancellationToken>());
    }

    // ── ack ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>ack follows the same header rule under morph-idm: the parameter is the role header.</summary>
    [Fact]
    public async Task Ack_AsRoleHeader_HandsTheParameterToTheResolverAsTheRoleHeader()
    {
        GivenProviderTreatsTheParameterAsTheRoleHeader();
        var instance = GivenInstance();
        instance.ArmLongPollAck(Guid.NewGuid());
        _longPollGate.IsAdmittedAsync(
                Arg.Any<Instance>(), Arg.Any<WorkflowDefinition>(), Arg.Any<State?>(),
                Arg.Any<Dictionary<string, string?>?>(), Arg.Any<Dictionary<string, string?>?>(),
                Arg.Any<Func<CancellationToken, Task<Result<IReadOnlyCollection<string>>>>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(true));

        await _sut.GetAuthorizeResultForInstanceAsync(
            Domain, Flow, Guid.NewGuid().ToString(), role: ClaimedRole,
            transitionKey: null, functionKey: null, version: null,
            checkQueryRoles: false, checkAck: true,
            requestContext: Context());

        await _roleResolver.Received().ResolveRolesAsync(
            Arg.Is<IReadOnlyDictionary<string, string?>?>(h => h != null && h["role"] == ClaimedRole),
            Arg.Any<CancellationToken>());
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

    /// <summary>
    /// ADDITIVE, not a fallback, even when the provider DID answer with roles — on an awaiting
    /// instance with no active SubFlow. This path used to go through the common role resolution,
    /// where a non-empty provider set discards the parameter, while the awaiting-parent-with-SubFlow
    /// path added it: the same caller and request got <c>[provider.role]</c> on one and
    /// <c>[provider.role, claimed]</c> on the other. Settled 2026-09-25: additive everywhere.
    /// </summary>
    [Fact]
    public async Task Ack_UnderTheDefaultProvider_AddsTheClaimedRoleToANonEmptyProviderSet()
    {
        GivenProviderAllowsFallback();
        _roleResolver.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok(["provider.role"]));
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
        roles.ShouldBe(["provider.role", ClaimedRole], ignoreOrder: true);
    }

    /// <summary>The other targets keep the fallback: a provider that answered wins over the parameter.</summary>
    [Fact]
    public async Task QueryRoles_UnderTheDefaultProvider_IgnoresTheParameterWhenTheProviderAnswered()
    {
        GivenProviderAllowsFallback();
        _roleResolver.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok(["provider.role"]));
        GivenInstance();

        await AuthorizeQueryRolesAsync(ClaimedRole);

        await _authManager.Received(1).IsQueryAllowedAsync(
            Arg.Any<WorkflowDefinition>(),
            Arg.Any<Instance>(),
            Arg.Is<IReadOnlyCollection<string>?>(r => r != null && r.Count == 1 && r.Contains("provider.role")),
            Arg.Any<AuthorizationRequestContext?>(),
            Arg.Any<CancellationToken>());
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private void GivenProviderTreatsTheParameterAsTheRoleHeader() =>
        _roleResolver.RoleParameterMode.Returns(RoleParameterMode.AsRoleHeader);

    private void GivenProviderAllowsFallback() =>
        _roleResolver.RoleParameterMode.Returns(RoleParameterMode.Fallback);

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
