using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Unit tests for BehalfOf predefined role evaluation:
/// $InstanceBehalfOfStarter and $PreviousBehalfOfUser.
/// </summary>
public sealed class TransitionAuthorizationManagerBehalfOfTests
{
    private readonly ICurrentUser _currentUser;
    private readonly IInstanceTransitionRepository _repo;
    private readonly TransitionAuthorizationManager _sut;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public TransitionAuthorizationManagerBehalfOfTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _repo = Substitute.For<IInstanceTransitionRepository>();
        _repo.GetLastCompletedManualTransitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns((InstanceTransition?)null);
        _sut = new TransitionAuthorizationManager(_currentUser, _repo);
    }

    private static Transition BuildTransition(string roleValue) =>
        JsonSerializer.Deserialize<Transition>($$"""
            {
              "key": "t1",
              "from": null,
              "target": "state2",
              "triggerType": "Manual",
              "versionStrategy": "None",
              "labels": [],
              "onExecutionTasks": [],
              "roles": [{"role": "{{roleValue}}", "grant": "allow"}]
            }
            """, JsonOptions)!;

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

    #region $InstanceBehalfOfStarter

    [Fact]
    public async Task InstanceBehalfOfStarter_WhenSubjectMatchesCreatedByBehalfOf_Allows()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.CreatedByBehalfOf = "behalf-alice";
        _currentUser.UserName.Returns("behalf-alice");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition(PredefinedInstanceRoles.InstanceBehalfOfStarter),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task InstanceBehalfOfStarter_WhenSubjectDoesNotMatchCreatedByBehalfOf_Denies()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.CreatedByBehalfOf = "behalf-alice";
        _currentUser.UserName.Returns("behalf-bob");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition(PredefinedInstanceRoles.InstanceBehalfOfStarter),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task InstanceBehalfOfStarter_WhenInstanceCreatedByBehalfOfIsNull_Denies()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.CreatedByBehalfOf = null;
        _currentUser.UserName.Returns("behalf-alice");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition(PredefinedInstanceRoles.InstanceBehalfOfStarter),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task InstanceBehalfOfStarter_WhenUserNameIsNull_Denies()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.CreatedByBehalfOf = "behalf-alice";
        _currentUser.UserName.Returns((string?)null);

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition(PredefinedInstanceRoles.InstanceBehalfOfStarter),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    #endregion

    #region $PreviousBehalfOfUser

    [Fact]
    public async Task PreviousBehalfOfUser_WhenSubjectMatchesPreviousTransitionBehalfOf_Allows()
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "flow", "1.0.0", "key");

        var lastTransition = InstanceTransition.Create(
            Guid.NewGuid(), instanceId, "t0", "state1",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"), JsonData.CreateFrom("{}"));
        lastTransition.CreatedByBehalfOf = "behalf-alice";

        _repo.GetLastCompletedManualTransitionAsync(instanceId, Arg.Any<CancellationToken>())
             .Returns(lastTransition);
        _currentUser.UserName.Returns("behalf-alice");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition(PredefinedInstanceRoles.PreviousBehalfOfUser),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task PreviousBehalfOfUser_WhenSubjectDoesNotMatchPreviousTransitionBehalfOf_Denies()
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "flow", "1.0.0", "key");

        var lastTransition = InstanceTransition.Create(
            Guid.NewGuid(), instanceId, "t0", "state1",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"), JsonData.CreateFrom("{}"));
        lastTransition.CreatedByBehalfOf = "behalf-alice";

        _repo.GetLastCompletedManualTransitionAsync(instanceId, Arg.Any<CancellationToken>())
             .Returns(lastTransition);
        _currentUser.UserName.Returns("behalf-bob");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition(PredefinedInstanceRoles.PreviousBehalfOfUser),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task PreviousBehalfOfUser_WhenNoPreviousTransition_Denies()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        _currentUser.UserName.Returns("behalf-alice");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition(PredefinedInstanceRoles.PreviousBehalfOfUser),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task PreviousBehalfOfUser_WhenPreviousTransitionBehalfOfIsNull_Denies()
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "flow", "1.0.0", "key");

        var lastTransition = InstanceTransition.Create(
            Guid.NewGuid(), instanceId, "t0", "state1",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"), JsonData.CreateFrom("{}"));
        lastTransition.CreatedByBehalfOf = null;

        _repo.GetLastCompletedManualTransitionAsync(instanceId, Arg.Any<CancellationToken>())
             .Returns(lastTransition);
        _currentUser.UserName.Returns("behalf-alice");

        var result = await _sut.IsTransitionAllowedForRoleAsync(
            BuildWorkflow(),
            BuildTransition(PredefinedInstanceRoles.PreviousBehalfOfUser),
            instance, null, cancellationToken: CancellationToken.None);

        result.ShouldBeFalse();
    }

    #endregion

    #region GetEffectiveCallerRolesForFieldVisibilityAsync - BehalfOf roles

    [Fact]
    public async Task GetEffectiveRoles_WhenSubjectIsInstanceBehalfOfStarter_AddsRole()
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "flow", "1.0.0", "key");
        instance.CreatedByBehalfOf = "behalf-alice";

        _currentUser.Roles.Returns(Array.Empty<string>());
        _currentUser.UserName.Returns("behalf-alice");
        _currentUser.ActorUserName.Returns((string?)null);

        var result = await _sut.GetEffectiveCallerRolesForFieldVisibilityAsync(instance, CancellationToken.None);

        result.ShouldContain(PredefinedInstanceRoles.InstanceBehalfOfStarter);
    }

    [Fact]
    public async Task GetEffectiveRoles_WhenSubjectIsPreviousBehalfOfUser_AddsRole()
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "flow", "1.0.0", "key");

        var lastTransition = InstanceTransition.Create(
            Guid.NewGuid(), instanceId, "t0", "state1",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"), JsonData.CreateFrom("{}"));
        lastTransition.CreatedByBehalfOf = "behalf-alice";

        _repo.GetLastCompletedManualTransitionAsync(instanceId, Arg.Any<CancellationToken>())
             .Returns(lastTransition);

        _currentUser.Roles.Returns(Array.Empty<string>());
        _currentUser.UserName.Returns("behalf-alice");
        _currentUser.ActorUserName.Returns((string?)null);

        var result = await _sut.GetEffectiveCallerRolesForFieldVisibilityAsync(instance, CancellationToken.None);

        result.ShouldContain(PredefinedInstanceRoles.PreviousBehalfOfUser);
    }

    [Fact]
    public async Task GetEffectiveRoles_WhenBothActorAndSubjectMatch_AddsAllFourPredefinedRoles()
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "flow", "1.0.0", "key");
        instance.CreatedBy = "alice";
        instance.CreatedByBehalfOf = "behalf-alice";

        var lastTransition = InstanceTransition.Create(
            Guid.NewGuid(), instanceId, "t0", "state1",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"), JsonData.CreateFrom("{}"));
        lastTransition.CreatedBy = "alice";
        lastTransition.CreatedByBehalfOf = "behalf-alice";

        _repo.GetLastCompletedManualTransitionAsync(instanceId, Arg.Any<CancellationToken>())
             .Returns(lastTransition);

        _currentUser.Roles.Returns(Array.Empty<string>());
        _currentUser.ActorUserName.Returns("alice");
        _currentUser.UserName.Returns("behalf-alice");

        var result = await _sut.GetEffectiveCallerRolesForFieldVisibilityAsync(instance, CancellationToken.None);

        result.ShouldContain(PredefinedInstanceRoles.InstanceStarter);
        result.ShouldContain(PredefinedInstanceRoles.PreviousUser);
        result.ShouldContain(PredefinedInstanceRoles.InstanceBehalfOfStarter);
        result.ShouldContain(PredefinedInstanceRoles.PreviousBehalfOfUser);
        result.Count.ShouldBe(4);
    }

    [Fact]
    public async Task GetEffectiveRoles_SingleRepositoryFetch_WhenBothActorAndSubjectPresent()
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "flow", "1.0.0", "key");
        instance.CreatedBy = "alice";
        instance.CreatedByBehalfOf = "behalf-alice";

        _currentUser.Roles.Returns(Array.Empty<string>());
        _currentUser.ActorUserName.Returns("alice");
        _currentUser.UserName.Returns("behalf-alice");

        _repo.GetLastCompletedManualTransitionAsync(instanceId, Arg.Any<CancellationToken>())
             .Returns((InstanceTransition?)null);

        await _sut.GetEffectiveCallerRolesForFieldVisibilityAsync(instance, CancellationToken.None);

        // Repository should only be called once even though both actor and behalf-of checks need it
        await _repo.Received(1).GetLastCompletedManualTransitionAsync(instanceId, Arg.Any<CancellationToken>());
    }

    #endregion
}
