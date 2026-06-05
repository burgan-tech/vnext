using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Unit tests for <see cref="TransitionAuthorizationManager.IsQueryAllowedAsync"/> — the shared queryRoles
/// gate used by the state/data/view/schema instance functions. Verifies state→workflow precedence,
/// empty-grants→allow, deny when no role matches, and multi-role any-allow.
/// </summary>
public sealed class TransitionAuthorizationManagerQueryRolesTests : IDisposable
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

    public TransitionAuthorizationManagerQueryRolesTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _repo = Substitute.For<IInstanceTransitionRepository>();
        _repo.GetLastCompletedManualTransitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns((InstanceTransition?)null);
        _sut = new TransitionAuthorizationManager(_currentUser, _repo);

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

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbientServiceProvider;

    private static WorkflowDefinition BuildWorkflow(string stateQueryRolesJson, string rootQueryRolesJson) =>
        JsonSerializer.Deserialize<WorkflowDefinition>($$"""
            {
              "type": "F",
              "timeout": null,
              "labels": [],
              "functions": [],
              "features": [],
              "states": [
                {
                  "key": "review",
                  "stateType": "intermediate",
                  "labels": [],
                  "transitions": [],
                  "queryRoles": {{stateQueryRolesJson}}
                }
              ],
              "sharedTransitions": [],
              "extensions": [],
              "queryRoles": {{rootQueryRolesJson}}
            }
            """, JsonOptions)!;

    private static Instance InReviewState()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.SetEffectiveState("review");
        return instance;
    }

    [Fact]
    public async Task NoGrants_Allows()
    {
        var wf = BuildWorkflow("[]", "[]");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), new[] { "anyone" })).ShouldBeTrue();
    }

    [Fact]
    public async Task NoGrants_NullRoles_Allows()
    {
        var wf = BuildWorkflow("[]", "[]");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), null)).ShouldBeTrue();
    }

    [Fact]
    public async Task StateGrant_Allows_WhenCallerMatches()
    {
        var wf = BuildWorkflow("""[{"role":"backoffice","grant":"allow"}]""", "[]");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), new[] { "backoffice" })).ShouldBeTrue();
    }

    [Fact]
    public async Task Denies_WhenNoCallerRoleMatches()
    {
        var wf = BuildWorkflow("""[{"role":"backoffice","grant":"allow"}]""", "[]");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), new[] { "customer" })).ShouldBeFalse();
    }

    [Fact]
    public async Task StateQueryRoles_OverrideWorkflowQueryRoles()
    {
        // Workflow root would allow "customer", but the state's own grants (override) only allow "backoffice".
        var wf = BuildWorkflow("""[{"role":"backoffice","grant":"allow"}]""", """[{"role":"customer","grant":"allow"}]""");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), new[] { "customer" })).ShouldBeFalse();
    }

    [Fact]
    public async Task WorkflowQueryRoles_UsedWhenStateHasNone()
    {
        var wf = BuildWorkflow("[]", """[{"role":"customer","grant":"allow"}]""");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), new[] { "customer" })).ShouldBeTrue();
    }

    [Fact]
    public async Task MultiRole_AnyAllowed_Allows()
    {
        var wf = BuildWorkflow("""[{"role":"backoffice","grant":"allow"}]""", "[]");
        (await _sut.IsQueryAllowedAsync(wf, InReviewState(), new[] { "x", "backoffice" })).ShouldBeTrue();
    }
}
