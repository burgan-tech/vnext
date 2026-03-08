using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Users;
using BBT.Workflow;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Unit tests for TransitionAuthorizationManager.GetEffectiveCallerRolesForFieldVisibilityAsync.
/// </summary>
public sealed class TransitionAuthorizationManagerEffectiveRolesTests
{
    private readonly ICurrentUser _currentUser;
    private readonly IInstanceTransitionRepository _instanceTransitionRepository;
    private readonly TransitionAuthorizationManager _sut;

    public TransitionAuthorizationManagerEffectiveRolesTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _instanceTransitionRepository = Substitute.For<IInstanceTransitionRepository>();
        _sut = new TransitionAuthorizationManager(_currentUser, _instanceTransitionRepository);
    }

    [Fact]
    public async Task GetEffectiveCallerRolesForFieldVisibilityAsync_WhenInstanceIsNull_ReturnsOnlyStaticRoles()
    {
        _currentUser.Roles.Returns(new[] { "maker", "approver" });

        var result = await _sut.GetEffectiveCallerRolesForFieldVisibilityAsync(null, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result.ShouldContain("maker");
        result.ShouldContain("approver");
        await _instanceTransitionRepository.DidNotReceive().GetLastCompletedManualTransitionAsync(Arg.Any<System.Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetEffectiveCallerRolesForFieldVisibilityAsync_WhenInstanceNullAndNoRoles_ReturnsEmptyList()
    {
        _currentUser.Roles.Returns((string[]?)null);

        var result = await _sut.GetEffectiveCallerRolesForFieldVisibilityAsync(null, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetEffectiveCallerRolesForFieldVisibilityAsync_WhenActorIsInstanceStarter_AddsInstanceStarterRole()
    {
        var instanceId = System.Guid.NewGuid();
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0", "key");
        instance.CreatedBy = "alice";

        _currentUser.Roles.Returns(new[] { "viewer" });
        _currentUser.ActorUserName.Returns("alice");
        _instanceTransitionRepository.GetLastCompletedManualTransitionAsync(instanceId, Arg.Any<CancellationToken>()).Returns((InstanceTransition?)null);

        var result = await _sut.GetEffectiveCallerRolesForFieldVisibilityAsync(instance, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldContain("viewer");
        result.ShouldContain(PredefinedInstanceRoles.InstanceStarter);
        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetEffectiveCallerRolesForFieldVisibilityAsync_WhenActorIsPreviousUser_AddsPreviousUserRole()
    {
        var instanceId = System.Guid.NewGuid();
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0","key");
        instance.CreatedBy = "bob";

        var lastTransition = InstanceTransition.Create(
            System.Guid.NewGuid(),
            instanceId,
            "t1",
            "state1",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}"));
        lastTransition.CreatedBy = "alice";

        _currentUser.Roles.Returns(new[] { "viewer" });
        _currentUser.ActorUserName.Returns("alice");
        _instanceTransitionRepository.GetLastCompletedManualTransitionAsync(instanceId, Arg.Any<CancellationToken>()).Returns(lastTransition);

        var result = await _sut.GetEffectiveCallerRolesForFieldVisibilityAsync(instance, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldContain("viewer");
        result.ShouldContain(PredefinedInstanceRoles.PreviousUser);
        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetEffectiveCallerRolesForFieldVisibilityAsync_WhenActorIsBothStarterAndPreviousUser_AddsBothPredefinedRoles()
    {
        var instanceId = System.Guid.NewGuid();
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0","key");
        instance.CreatedBy = "alice";

        var lastTransition = InstanceTransition.Create(
            System.Guid.NewGuid(),
            instanceId,
            "t1",
            "state1",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}"));
        lastTransition.CreatedBy = "alice";

        _currentUser.Roles.Returns(Array.Empty<string>());
        _currentUser.ActorUserName.Returns("alice");
        _instanceTransitionRepository.GetLastCompletedManualTransitionAsync(instanceId, Arg.Any<CancellationToken>()).Returns(lastTransition);

        var result = await _sut.GetEffectiveCallerRolesForFieldVisibilityAsync(instance, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldContain(PredefinedInstanceRoles.InstanceStarter);
        result.ShouldContain(PredefinedInstanceRoles.PreviousUser);
        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetEffectiveCallerRolesForFieldVisibilityAsync_WhenActorMatchesNeither_ReturnsOnlyStaticRoles()
    {
        var instanceId = System.Guid.NewGuid();
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0","key");
        instance.CreatedBy = "bob";

        var lastTransition = InstanceTransition.Create(
            System.Guid.NewGuid(),
            instanceId,
            "t1",
            "state1",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}"));
        lastTransition.CreatedBy = "charlie";

        _currentUser.Roles.Returns(new[] { "viewer" });
        _currentUser.ActorUserName.Returns("alice");
        _instanceTransitionRepository.GetLastCompletedManualTransitionAsync(instanceId, Arg.Any<CancellationToken>()).Returns(lastTransition);

        var result = await _sut.GetEffectiveCallerRolesForFieldVisibilityAsync(instance, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);
        result.ShouldContain("viewer");
        result.ShouldNotContain(PredefinedInstanceRoles.InstanceStarter);
        result.ShouldNotContain(PredefinedInstanceRoles.PreviousUser);
    }
}
