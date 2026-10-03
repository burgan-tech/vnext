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

namespace BBT.Workflow.Authorization;

/// <summary>
/// The <c>allOf</c> / <c>anyOf</c> combinators evaluated through the instance-bound evaluator, with
/// Kleene three-valued leaves: a role-bound leaf (static role, <c>$role.</c>) is <c>Unknown</c> for a
/// caller with no roles; identity leaves are always Yes or No. A DENY fires on Yes OR Unknown, an ALLOW
/// admits only on Yes.
/// <para>
/// Every row runs against one instance: <c>CreatedBy = u-ali</c>, <c>CreatedByBehalfOf = c-acme</c>,
/// <c>Data.customerId = u-veli</c>. <c>actor</c> is <c>ICurrentUser.ActorUserName</c>, <c>subject</c>
/// is <c>ICurrentUser.UserName</c>.
/// </para>
/// </summary>
public sealed class RoleGrantCombinatorTests : IDisposable
{
    // A — a customer may act only on an instance they started themselves.
    private const string A = """
        [{"allOf":[{"role":"customer-role"},{"role":"$InstanceStarter"}],"grant":"allow"}]
        """;

    // B — corporate: an ops user acting for the starting company, OR the company's own customer acting
    // for it (no role needed).
    private const string B = """
        [{"allOf":[{"role":"corporate.ops"},{"role":"$InstanceBehalfOfStarter"}],"grant":"allow"},
         {"allOf":[{"role":"$InstanceBehalfOfStarter"},{"role":"$user.$.context.Instance.Data.customerId"}],"grant":"allow"}]
        """;

    // C — either the starter or whoever the instance was started on behalf of.
    private const string C = """
        [{"anyOf":[{"role":"$InstanceStarter"},{"role":"$InstanceBehalfOfStarter"}],"grant":"allow"}]
        """;

    // D — four eyes: makers may act, but not the maker who started the instance.
    private const string D = """
        [{"role":"maker","grant":"allow"},
         {"allOf":[{"role":"maker"},{"role":"$InstanceStarter"}],"grant":"deny"}]
        """;

    // F — deny-only: a blocked user is refused only on the instances they started.
    private const string F = """
        [{"allOf":[{"role":"blocked"},{"role":"$InstanceStarter"}],"grant":"deny"}]
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly ICurrentUser _currentUser;
    private readonly IInstanceTransitionRepository _repo;
    private readonly TransitionAuthorizationManager _sut;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public RoleGrantCombinatorTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _repo = Substitute.For<IInstanceTransitionRepository>();
        _repo.GetLastCompletedManualTransitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns((InstanceTransition?)null);
        _sut = new TransitionAuthorizationManager(_currentUser, _repo);

        // Instance.SeedData goes through the SchemaValidation aspect, which needs an ambient UoW manager.
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mockUoWManager);
        services.AddSingleton(Substitute.For<BBT.Workflow.Caching.IComponentCacheStore>());
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbientServiceProvider;

    private static List<RoleGrant> Grants(string json) =>
        JsonSerializer.Deserialize<List<RoleGrant>>(json, JsonOptions)!;

    private static Instance NewInstance()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.CreatedBy = "u-ali";
        instance.CreatedByBehalfOf = "c-acme";
        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("""{"customerId":"u-veli"}"""), VersionStrategy.None);
        return instance;
    }

    [Theory]
    // A — customer allOf
    [InlineData(A, "customer-role", "u-ali", "u-ali", true)]
    [InlineData(A, "customer-role", "u-x", "u-ali", false)]
    [InlineData(A, null, "u-ali", "u-ali", false)]
    [InlineData(A, "customer-role", "u-x", "c-acme", false)] // a subject match is not $InstanceStarter
    // B — corporate, two allOf OR'd
    [InlineData(B, "corporate.ops", "u-ops", "c-acme", true)]
    [InlineData(B, null, "u-veli", "c-acme", true)] // role-less, the second allOf is Yes
    [InlineData(B, "corporate.ops", "u-veli", "u-x", false)]
    // C — anyOf
    [InlineData(C, null, "u-ali", "u-x", true)]
    [InlineData(C, null, "u-x", "c-acme", true)]
    [InlineData(C, null, "u-x", "u-x", false)]
    // D — four eyes: allow maker + deny allOf[maker, $InstanceStarter]
    [InlineData(D, "maker", "u-x", "u-x", true)]
    [InlineData(D, "maker", "u-ali", "u-x", false)]
    [InlineData(D, null, "u-x", "u-x", false)] // deny No (Unknown AND No), allow Unknown → refused
    // F — deny-only allOf[blocked, $InstanceStarter]
    [InlineData(F, "blocked", "u-x", "u-x", true)]
    [InlineData(F, "blocked", "u-ali", "u-x", false)]
    [InlineData(F, null, "u-x", "u-x", true)] // Kleene: Unknown AND No = No → the blacklist admits
    [InlineData(F, null, "u-ali", "u-x", false)] // Unknown AND Yes = Unknown → refused
    public async Task Matrix(string grants, string? role, string actor, string subject, bool expected)
    {
        _currentUser.ActorUserName.Returns(actor);
        _currentUser.UserName.Returns(subject);
        var set = Grants(grants);

        var evaluator = await _sut.CreateEvaluatorAsync(NewInstance(), null, null, set, CancellationToken.None);

        evaluator.IsRoleAllowed(role, set).ShouldBe(expected);
    }

    [Fact]
    public async Task PreviousUser_inside_allOf_triggers_prefetch()
    {
        var instance = NewInstance();
        _repo.GetLastCompletedManualTransitionAsync(instance.Id, Arg.Any<CancellationToken>())
             .Returns(PreviousTransitionBy(instance.Id, "u-ayse"));
        _currentUser.ActorUserName.Returns("u-ayse");

        var set = Grants("""
            [{"role":"maker","grant":"allow"},
             {"allOf":[{"role":"maker"},{"role":"$PreviousUser"}],"grant":"deny"}]
            """);

        var evaluator = await _sut.CreateEvaluatorAsync(instance, null, null, set, CancellationToken.None);

        // The maker who made the previous step cannot also make this one.
        evaluator.IsRoleAllowed("maker", set).ShouldBeFalse();
        await _repo.Received(1).GetLastCompletedManualTransitionAsync(instance.Id, Arg.Any<CancellationToken>());
    }

    private static InstanceTransition PreviousTransitionBy(Guid instanceId, string createdBy)
    {
        var transition = InstanceTransition.Create(
            Guid.NewGuid(), instanceId, "t0", "state1",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"), JsonData.CreateFrom("{}"));
        transition.CreatedBy = createdBy;
        return transition;
    }
}
