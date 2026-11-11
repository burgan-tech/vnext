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
/// Workflow-scoped <c>authorize?type=instance</c> (vnext#1203): a gateway asks whether the caller may
/// START an instance, before any instance exists, from the workflow's own <c>startTransition.roles</c>.
/// </summary>
/// <remarks>
/// Runs the REAL <see cref="TransitionAuthorizationManager"/>, because the decisions pinned here are the
/// evaluator's no-instance rules, not plumbing:
/// <list type="bullet">
///   <item>no grants → allow, as for every other transition;</item>
///   <item>static roles: DENY wins over ALLOW across the caller's whole role set;</item>
///   <item>instance-bound leaves (<c>$InstanceStarter</c>, <c>$PreviousUser</c>, <c>$user.</c>, …) have
///   nothing to resolve against and never match — they neither admit nor refuse;</item>
///   <item><c>$Everyone</c> matches every caller.</item>
/// </list>
/// </remarks>
public sealed class AuthorizeStartTransitionTests : IDisposable
{
    private const string Domain = "core";
    private const string Flow = "flow";

    private readonly IComponentCacheStore _componentCache = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly ICallerRoleResolver _roleResolver = Substitute.For<ICallerRoleResolver>();
    private readonly AuthorizeAppService _sut;
    private readonly IServiceProvider? _previousAmbient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public AuthorizeStartTransitionTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        services.AddSingleton(uowManager);
        var provider = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = provider;

        // A caller with a known identity: proves the identity-bound leaves are not matched against it.
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.ActorUserName.Returns("alice");
        currentUser.UserName.Returns("alice");

        var transitionRepo = Substitute.For<IInstanceTransitionRepository>();
        transitionRepo.GetLastCompletedManualTransitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((InstanceTransition?)null);

        _sut = new AuthorizeAppService(
            provider,
            Substitute.For<IRuntimeInfoProvider>(),
            _componentCache,
            _instanceRepository,
            new TransitionAuthorizationManager(currentUser, transitionRepo),
            Substitute.For<IAuthorizeGateway>(),
            _roleResolver,
            Substitute.For<ILongPollInteractionGate>(),
            Substitute.For<ILogger<AuthorizeAppService>>());
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbient;

    [Fact]
    public async Task NoRolesOnTheStartTransition_Allows()
    {
        GivenStartRoles("[]");
        GivenCallerRoles();

        (await AuthorizeStartAsync()).Allowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("maker", true)]
    [InlineData("checker", false)]
    public async Task StaticAllowGrant_AdmitsOnlyTheNamedRole(string callerRole, bool expected)
    {
        GivenStartRoles("""[{"role":"maker","grant":"allow"}]""");
        GivenCallerRoles(callerRole);

        (await AuthorizeStartAsync()).Allowed.ShouldBe(expected);
    }

    [Fact]
    public async Task ADeniedRole_IsNotBoughtBackByAnAllowedOne()
    {
        GivenStartRoles("""[{"role":"maker","grant":"allow"},{"role":"blocked","grant":"deny"}]""");
        GivenCallerRoles("maker", "blocked");

        (await AuthorizeStartAsync()).Allowed.ShouldBeFalse();
    }

    /// <summary>
    /// An instance-bound ALLOW never admits: with no instance there is no starter to compare against, so
    /// a start transition guarded only by it is closed to everyone (fail-closed, not "list is empty").
    /// </summary>
    [Theory]
    [InlineData("$InstanceStarter")]
    [InlineData("$PreviousUser")]
    [InlineData("$InstanceBehalfOfStarter")]
    [InlineData("$PreviousBehalfOfUser")]
    [InlineData("$user.$.context.Instance.Data.ownerUserId")]
    public async Task InstanceBoundAllowAlone_DoesNotAdmit(string leaf)
    {
        GivenStartRoles($$"""[{"role":"{{leaf}}","grant":"allow"}]""");
        GivenCallerRoles("maker");

        (await AuthorizeStartAsync()).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task InstanceBoundAllow_BesideAStaticAllow_LeavesTheStaticGrantDeciding()
    {
        GivenStartRoles("""[{"role":"$InstanceStarter","grant":"allow"},{"role":"maker","grant":"allow"}]""");

        GivenCallerRoles("maker");
        (await AuthorizeStartAsync()).Allowed.ShouldBeTrue();

        GivenCallerRoles("checker");
        (await AuthorizeStartAsync()).Allowed.ShouldBeFalse();
    }

    /// <summary>An instance-bound DENY never refuses either: it has nothing to fire on.</summary>
    [Theory]
    [InlineData("$PreviousUser")]
    [InlineData("$user.$.context.Instance.Data.blockedUserId")]
    public async Task InstanceBoundDeny_DoesNotRefuse(string leaf)
    {
        GivenStartRoles($$"""[{"role":"{{leaf}}","grant":"deny"},{"role":"maker","grant":"allow"}]""");
        GivenCallerRoles("maker");

        (await AuthorizeStartAsync()).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task InstanceBoundLeafInsideAnAllOf_MakesTheCombinatorUnsatisfiable()
    {
        GivenStartRoles("""[{"allOf":[{"role":"maker"},{"role":"$InstanceStarter"}],"grant":"allow"}]""");
        GivenCallerRoles("maker");

        (await AuthorizeStartAsync()).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Everyone_AdmitsARoleLessCaller()
    {
        GivenStartRoles("""[{"role":"$Everyone","grant":"allow"}]""");
        GivenCallerRoles();

        (await AuthorizeStartAsync()).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task AsksForTheRequestedVersion_AndNeverLoadsAnInstance()
    {
        GivenStartRoles("[]");
        GivenCallerRoles();

        await _sut.GetStartAuthorizeResultAsync(Domain, Flow, role: null, version: " 1.2.0 ");

        await _componentCache.Received(1).GetFlowAsync(Domain, Flow, "1.2.0", Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceiveWithAnyArgs().FindByIdentifierAsync(default, default);
    }

    [Fact]
    public async Task AnUnknownWorkflow_FailsWithTheLookupError()
    {
        _componentCache.GetFlowAsync(Domain, Flow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Fail(Error.NotFound("Workflow:404", "not found")));
        GivenCallerRoles();

        var result = await _sut.GetStartAuthorizeResultAsync(Domain, Flow, role: null);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Workflow:404");
    }

    private async Task<AuthorizeOutput> AuthorizeStartAsync()
    {
        var result = await _sut.GetStartAuthorizeResultAsync(
            Domain, Flow, role: null, version: null, requestContext: new AuthorizationRequestContext());
        result.IsSuccess.ShouldBeTrue();
        return result.Value!;
    }

    private void GivenCallerRoles(params string[] roles) =>
        _roleResolver.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok(roles));

    private void GivenStartRoles(string rolesJson) =>
        _componentCache.GetFlowAsync(Domain, Flow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(BuildWorkflow(rolesJson)));

    private static WorkflowDefinition BuildWorkflow(string startRolesJson) =>
        JsonSerializer.Deserialize<WorkflowDefinition>($$"""
            {
              "key": "flow",
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
              "queryRoles": [],
              "startTransition": { "key": "start", "from": null, "target": "waiting",
                                   "triggerType": "Manual", "versionStrategy": "Patch",
                                   "labels": [], "onExecutionTasks": [], "view": null,
                                   "roles": {{startRolesJson}} }
            }
            """, JsonOptions)!;
}
