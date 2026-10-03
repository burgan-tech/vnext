using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
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
/// Unit tests for <see cref="AuthorizeAppService"/>'s behaviour on an instance with an active SubFlow.
/// <para>
/// These pin the descent rules: queryRoles is decided at the deepest active leaf only (K9 — a SubFlow
/// is part of its parent's process, so the levels above are never ANDed), a parent-declared override
/// RETURNED at depth 1 in the past (so a grandchild's gate never ran), and the overrides were read from
/// the parent's definition rather than from the child's stamp (so a directly addressed leaf got the
/// opposite verdict).
/// </para>
/// </summary>
public sealed class AuthorizeAppServiceSubflowTests : IDisposable
{
    private const string Domain = "core";
    private const string Flow = "parent-flow";

    private readonly IRuntimeInfoProvider _runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
    private readonly IComponentCacheStore _componentCache = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly ITransitionAuthorizationManager _authManager = Substitute.For<ITransitionAuthorizationManager>();
    private readonly IAuthorizeGateway _gateway = Substitute.For<IAuthorizeGateway>();
    private readonly ICallerRoleResolver _roleResolver = Substitute.For<ICallerRoleResolver>();
    private readonly ILongPollInteractionGate _longPollGate = Substitute.For<ILongPollInteractionGate>();
    private readonly AuthorizeAppService _sut;
    private readonly IServiceProvider? _previousAmbient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public AuthorizeAppServiceSubflowTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockUoW));
        services.AddSingleton(mockUoWManager);
        var provider = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = provider;

        _roleResolver.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok(["approver"]));

        _sut = new AuthorizeAppService(
            provider,
            _runtimeInfo,
            _componentCache,
            _instanceRepository,
            _authManager,
            _gateway,
            _roleResolver,
            _longPollGate,
            Substitute.For<ILogger<AuthorizeAppService>>());
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbient;

    // ---------------------------------------------------------------- fixtures

    private static WorkflowDefinition BuildParentWorkflow() =>
        JsonSerializer.Deserialize<WorkflowDefinition>("""
            {
              "key": "parent-flow",
              "type": "F",
              "timeout": null,
              "labels": [],
              "functions": [],
              "features": [],
              "states": [
                { "key": "waiting", "stateType": "intermediate", "labels": [], "transitions": [] }
              ],
              "sharedTransitions": [],
              "extensions": [],
              "queryRoles": []
            }
            """, JsonOptions)!;

    /// <summary>A parent parked in <c>waiting</c> with one open SubFlow correlation.</summary>
    private static Instance ParentWithActiveSubflow()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "parent-key");
        instance.SetEffectiveState("waiting");
        instance.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            parentState: "waiting",
            subFlowInstanceId: Guid.NewGuid(),
            subFlowType: "S",
            subFlowDomain: Domain,
            subFlowName: "child-flow",
            subFlowVersion: "1.0.0"));
        return instance;
    }

    private void GivenInstance(Instance instance)
    {
        _instanceRepository.FindByIdentifierAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(instance);
        _componentCache.GetFlowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(BuildParentWorkflow()));
    }

    private void GivenRootQueryVerdict(bool allowed) =>
        _authManager.IsQueryAllowedAsync(
                Arg.Any<WorkflowDefinition>(), Arg.Any<Instance>(), Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(allowed);

    private void GivenLeafVerdict(bool allowed) =>
        _gateway.GetAuthorizeResultForInstanceAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(Result<AuthorizeOutput>.Ok(new AuthorizeOutput { Allowed = allowed }));

    private Task<Result<AuthorizeOutput>> AuthorizeQueryRolesAsync() =>
        _sut.GetAuthorizeResultForInstanceAsync(
            Domain, Flow, Guid.NewGuid().ToString(), role: string.Empty,
            transitionKey: null, functionKey: null, version: null,
            checkQueryRoles: true, checkAck: false,
            requestContext: new AuthorizationRequestContext(new Dictionary<string, string?>()));

    private Task<Result<AuthorizeOutput>> AuthorizeAckAsync() =>
        _sut.GetAuthorizeResultForInstanceAsync(
            Domain, Flow, Guid.NewGuid().ToString(), role: string.Empty,
            transitionKey: null, functionKey: null, version: null,
            checkQueryRoles: false, checkAck: true,
            requestContext: new AuthorizationRequestContext(new Dictionary<string, string?>()));

    // ------------------------------------------------- A1: leaf-only (K9)

    /// <summary>
    /// K9. A SubFlow is part of its parent's process, so while an instance is inside one the queryRoles
    /// decision is the deepest active leaf's — the root's own queryRoles are not evaluated and the
    /// gateway forward is taken regardless of what the root would have said.
    /// </summary>
    [Fact]
    public async Task QueryRoles_RootDenies_LeafAllows_Allows()
    {
        GivenInstance(ParentWithActiveSubflow());
        GivenRootQueryVerdict(false);
        GivenLeafVerdict(true);

        var result = await AuthorizeQueryRolesAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Allowed.ShouldBeTrue();

        await _authManager.DidNotReceive().IsQueryAllowedAsync(
            Arg.Any<WorkflowDefinition>(), Arg.Any<Instance>(), Arg.Any<IReadOnlyCollection<string>?>(),
            Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>());
        await _gateway.Received(1).GetAuthorizeResultForInstanceAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Is<bool>(q => q), Arg.Any<bool>(),
            Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A leaf refusal is the refusal, whatever the root would have said.</summary>
    [Fact]
    public async Task QueryRoles_RootAllows_LeafDenies_Denies()
    {
        GivenInstance(ParentWithActiveSubflow());
        GivenRootQueryVerdict(true);
        GivenLeafVerdict(false);

        (await AuthorizeQueryRolesAsync()).Value!.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task QueryRoles_RootAndLeafAllow_Allows()
    {
        GivenInstance(ParentWithActiveSubflow());
        GivenRootQueryVerdict(true);
        GivenLeafVerdict(true);

        (await AuthorizeQueryRolesAsync()).Value!.Allowed.ShouldBeTrue();
    }

    /// <summary>
    /// Every level that still has an active SubFlow only forwards; none of them evaluates. With a deny-all
    /// manager at every level above the leaf (A and B deny, C allows) the deepest leaf alone decides.
    /// </summary>
    [Fact]
    public async Task QueryRoles_ThreeLevels_OnlyDeepestLeafDecides()
    {
        GivenInstance(ParentWithActiveSubflow());
        GivenRootQueryVerdict(false);
        GivenLeafVerdict(true);

        (await AuthorizeQueryRolesAsync()).Value!.Allowed.ShouldBeTrue();

        await _authManager.DidNotReceiveWithAnyArgs().IsQueryAllowedAsync(
            default!, default!, default, default, default);
    }

    // --------------------------------------------------------------- ack target

    /// <summary>
    /// `ack` mirrors the acknowledge endpoint's own descent rule — "is this the instance that paused",
    /// not "does it have a subflow". When this instance is awaiting, the gate decides here and the
    /// chain below is irrelevant, because a deeper child is not what the acknowledge resumes.
    /// </summary>
    [Fact]
    public async Task Ack_AwaitingHere_EvaluatesLocallyAndDoesNotDescend()
    {
        var instance = ParentWithActiveSubflow();
        instance.ArmLongPollAck(Guid.NewGuid());
        GivenInstance(instance);
        _longPollGate.IsAdmittedAsync(
                Arg.Any<Instance>(), Arg.Any<WorkflowDefinition>(), Arg.Any<State?>(),
                Arg.Any<Dictionary<string, string?>?>(), Arg.Any<Dictionary<string, string?>?>(),
                Arg.Any<Func<CancellationToken, Task<Result<IReadOnlyCollection<string>>>>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(false));

        var result = await AuthorizeAckAsync();

        result.Value!.Allowed.ShouldBeFalse();
        await _gateway.DidNotReceive().GetAuthorizeResultForInstanceAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Not awaiting here but a child might be: descend, exactly as the endpoint does.</summary>
    [Fact]
    public async Task Ack_NotAwaitingButHasSubflow_Descends()
    {
        GivenInstance(ParentWithActiveSubflow());
        GivenLeafVerdict(true);

        (await AuthorizeAckAsync()).Value!.Allowed.ShouldBeTrue();

        await _gateway.Received(1).GetAuthorizeResultForInstanceAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(),
            Arg.Is<bool>(a => a),
            Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Nothing awaiting anywhere: the endpoint answers Ok() idempotently, so the pre-flight must answer
    /// allowed. A refusal here would have the middle tier 403 a call the runtime accepts.
    /// </summary>
    [Fact]
    public async Task Ack_NothingAwaitingAnywhere_Allows()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "no-subflow");
        instance.SetEffectiveState("waiting");
        GivenInstance(instance);

        (await AuthorizeAckAsync()).Value!.Allowed.ShouldBeTrue();
    }

    // ------------------------------------------------------------- target validation

    [Fact]
    public async Task TwoTargets_IsRejected()
    {
        GivenInstance(ParentWithActiveSubflow());

        var result = await _sut.GetAuthorizeResultForInstanceAsync(
            Domain, Flow, Guid.NewGuid().ToString(), role: string.Empty,
            transitionKey: null, functionKey: null, version: null,
            checkQueryRoles: true, checkAck: true);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizeRequiresExactlyOneTarget);
    }

    [Fact]
    public async Task NoTarget_IsRejected()
    {
        GivenInstance(ParentWithActiveSubflow());

        var result = await _sut.GetAuthorizeResultForInstanceAsync(
            Domain, Flow, Guid.NewGuid().ToString(), role: string.Empty,
            transitionKey: null, functionKey: null, version: null,
            checkQueryRoles: false, checkAck: false);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizeRequiresExactlyOneTarget);
    }
}
