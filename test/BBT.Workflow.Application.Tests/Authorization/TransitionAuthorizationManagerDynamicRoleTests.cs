using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Unit tests for dynamic role grant evaluation in TransitionAuthorizationManager.
/// Dynamic roles use the format: $user.$.context.*, $userBehalfOf.$.context.*, $role.$.context.*
/// </summary>
public sealed class TransitionAuthorizationManagerDynamicRoleTests : IDisposable
{
    private readonly ICurrentUser _currentUser;
    private readonly IInstanceTransitionRepository _repo;
    private readonly TransitionAuthorizationManager _sut;
    private readonly IServiceProvider? _previousAmbientServiceProvider;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public TransitionAuthorizationManagerDynamicRoleTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _repo = Substitute.For<IInstanceTransitionRepository>();
        _repo.GetLastCompletedManualTransitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns((InstanceTransition?)null);
        _sut = new TransitionAuthorizationManager(_currentUser, _repo);

        // Required for PostSharp SchemaValidation aspect used by Instance.AddData
        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockUoW));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mockUoWManager);
        services.AddSingleton(Substitute.For<BBT.Workflow.Caching.IComponentCacheStore>());
        services.AddSingleton(Substitute.For<BBT.Workflow.DefinitionContext.IWorkflowContext>());
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
    }

    private static Instance CreateInstance(string dataJson)
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom(dataJson), VersionStrategy.None);
        return instance;
    }

    private static Transition BuildTransition(string roleValue, string grant = "allow")
    {
        var json = $$"""
            {
              "key": "t1",
              "from": null,
              "target": "state2",
              "triggerType": "Manual",
              "versionStrategy": "None",
              "labels": [],
              "onExecutionTasks": [],
              "roles": [{"role": "{{roleValue}}", "grant": "{{grant}}"}]
            }
            """;
        return JsonSerializer.Deserialize<Transition>(json, JsonOptions)!;
    }

    private static WorkflowDefinition BuildWorkflow() =>
        JsonSerializer.Deserialize<WorkflowDefinition>("""
            {
              "type": "F",
              "timeout": null,
              "labels": [],
              "functions": [],
              "features": [],
              "states": [],
              "sharedTransitions": [],
              "extensions": [],
              "queryRoles": []
            }
            """, JsonOptions)!;

    #region $user qualifier

    [Fact]
    public async Task DynamicUser_WhenActorMatchesInstanceDataValue_Allows()
    {
        _currentUser.ActorUserName.Returns("alice");
        var instance = CreateInstance("""{"customer": {"ownerUserId": "alice"}}""");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition("$user.$.context.Instance.Data.customer.ownerUserId"),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task DynamicUser_WhenActorDoesNotMatchInstanceDataValue_Denies()
    {
        _currentUser.ActorUserName.Returns("bob");
        var instance = CreateInstance("""{"customer": {"ownerUserId": "alice"}}""");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition("$user.$.context.Instance.Data.customer.ownerUserId"),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task DynamicUser_WithArrayWildcard_WhenActorIsInArray_Allows()
    {
        _currentUser.ActorUserName.Returns("charlie");
        var instance = CreateInstance("""{"assignedUsers": [{"userId": "alice"}, {"userId": "charlie"}]}""");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition("$user.$.context.Instance.Data.assignedUsers[*].userId"),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task DynamicUser_WithArrayWildcard_WhenActorNotInArray_Denies()
    {
        _currentUser.ActorUserName.Returns("dave");
        var instance = CreateInstance("""{"assignedUsers": [{"userId": "alice"}, {"userId": "charlie"}]}""");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition("$user.$.context.Instance.Data.assignedUsers[*].userId"),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task DynamicUser_WhenPathNotFound_Denies()
    {
        _currentUser.ActorUserName.Returns("alice");
        var instance = CreateInstance("""{"other": "value"}""");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition("$user.$.context.Instance.Data.customer.ownerUserId"),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    #endregion

    #region $userBehalfOf qualifier

    [Fact]
    public async Task DynamicUserBehalfOf_WhenSubjectMatchesInstanceDataValue_Allows()
    {
        _currentUser.UserName.Returns("behalf-user");
        var instance = CreateInstance("""{"customer": {"behalfOfId": "behalf-user"}}""");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition("$userBehalfOf.$.context.Instance.Data.customer.behalfOfId"),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task DynamicUserBehalfOf_WhenSubjectDoesNotMatch_Denies()
    {
        _currentUser.UserName.Returns("other-user");
        var instance = CreateInstance("""{"customer": {"behalfOfId": "behalf-user"}}""");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition("$userBehalfOf.$.context.Instance.Data.customer.behalfOfId"),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    #endregion

    #region $role qualifier

    [Fact]
    public async Task DynamicRole_WhenCallerRoleMatchesInstanceDataValue_Allows()
    {
        var instance = CreateInstance("""{"permissions": {"requiredRole": "maker"}}""");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition("$role.$.context.Instance.Data.permissions.requiredRole"),
            instance, "maker", cancellationToken: CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task DynamicRole_CaseInsensitiveMatch_Allows()
    {
        var instance = CreateInstance("""{"permissions": {"requiredRole": "Maker"}}""");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition("$role.$.context.Instance.Data.permissions.requiredRole"),
            instance, "maker", cancellationToken: CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task DynamicRole_WithArrayWildcard_WhenAnyRoleMatches_Allows()
    {
        var instance = CreateInstance("""{"approvers": [{"role": "viewer"}, {"role": "approver"}]}""");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition("$role.$.context.Instance.Data.approvers[*].role"),
            instance, "approver", cancellationToken: CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task DynamicRole_WhenCallerRoleNotInArray_Denies()
    {
        var instance = CreateInstance("""{"approvers": [{"role": "viewer"}, {"role": "approver"}]}""");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition("$role.$.context.Instance.Data.approvers[*].role"),
            instance, "maker", cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    #endregion

    #region DENY semantics

    [Fact]
    public async Task DynamicUser_WithDenyGrant_WhenActorMatches_Denies()
    {
        _currentUser.ActorUserName.Returns("alice");
        var instance = CreateInstance("""{"blockedUser": "alice"}""");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition("$user.$.context.Instance.Data.blockedUser", grant: "deny"),
            instance, "alice", cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    #endregion

    #region Transition path

    [Fact]
    public async Task DynamicRole_TransitionKeyPath_WhenMatchesCallerRole_Allows()
    {
        var instance = CreateInstance("{}");
        var json = """
            {
              "key": "approve",
              "from": null,
              "target": "approved",
              "triggerType": "Manual",
              "versionStrategy": "None",
              "labels": [],
              "onExecutionTasks": [],
              "roles": [{"role": "$role.$.context.Transition.Key", "grant": "allow"}]
            }
            """;
        var transition = JsonSerializer.Deserialize<Transition>(json, JsonOptions)!;

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(), transition, instance, "approve", cancellationToken: CancellationToken.None);

        result.ShouldBeTrue();
    }

    #endregion

    #region Instance null - dynamic roles always deny

    [Fact]
    public async Task DynamicRole_WhenInstanceIsNull_Denies()
    {
        // Without instance, EvaluateRolesStatic is used, which won't match dynamic role strings
        var transition = BuildTransition("$user.$.context.Instance.Data.owner");
        _currentUser.ActorUserName.Returns("alice");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(), transition, null, "alice", cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    #endregion
}
