using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Extentions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for InstanceQueryAppService.GetInstanceHistoryAsync.
/// Verifies that the response contains the actual state transitions
/// (InstanceTransition entities) instead of instance data versions.
/// </summary>
public class InstanceQueryAppServiceHistoryTests : IDisposable
{
    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";

    private readonly IRuntimeInfoProvider _runtimeInfoProvider;
    private readonly IInstanceRepository _instanceRepository;
    private readonly IInstanceTransitionRepository _instanceTransitionRepository;
    private readonly InstanceQueryAppService _service;
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public InstanceQueryAppServiceHistoryTests()
    {
        _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
        _instanceRepository = Substitute.For<IInstanceRepository>();
        _instanceTransitionRepository = Substitute.For<IInstanceTransitionRepository>();

        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager
            .BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockUoW));

        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        _ambientServiceProvider = services.BuildServiceProvider();

        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        _service = new InstanceQueryAppService(
            serviceProvider: _ambientServiceProvider,
            runtimeInfoProvider: _runtimeInfoProvider,
            componentCacheStore: Substitute.For<IComponentCacheStore>(),
            instanceRepository: _instanceRepository,
            instanceTransitionRepository: _instanceTransitionRepository,
            instanceCorrelationRepository: Substitute.For<IInstanceCorrelationRepository>(),
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            instanceQueryGateway: Substitute.For<IInstanceQueryGateway>(),
            viewContentResolutionService: Substitute.For<IViewContentResolutionService>(),
            taskConditionService: Substitute.For<ITaskConditionService>(),
            urlTemplateBuilder: Substitute.For<IUrlTemplateBuilder>(),
            currentSchema: Substitute.For<ICurrentSchema>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            paginationLinkGenerator: Substitute.For<BBT.Aether.Application.Pagination.IPaginationLinkGenerator>(),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task GetInstanceHistoryAsync_WhenInstanceNotFound_ReturnsFailure()
    {
        var input = new GetInstanceHistoryInput
        {
            Domain = TestDomain,
            Workflow = TestWorkflow,
            Instance = Guid.NewGuid().ToString()
        };

        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(input.Instance, Arg.Any<CancellationToken>())
            .Returns((Instance?)null);

        var result = await _service.GetInstanceHistoryAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        await _instanceTransitionRepository.DidNotReceive()
            .GetByInstanceIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstanceHistoryAsync_WhenInstanceFound_MapsTransitionsToDto()
    {
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");

        var t1 = InstanceTransition.Create(
            id: Guid.NewGuid(),
            instanceId: instance.Id,
            transitionId: "approve",
            fromState: "draft",
            triggerType: TriggerType.Manual,
            body: new JsonData("{\"amount\":100}"),
            header: new JsonData("{\"x-request-id\":\"abc\"}"));
        t1.Completed("approved");

        var t2 = InstanceTransition.Create(
            id: Guid.NewGuid(),
            instanceId: instance.Id,
            transitionId: "complete",
            fromState: "approved",
            triggerType: TriggerType.Automatic,
            body: new JsonData("{}"),
            header: new JsonData("{}"));

        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);

        _instanceTransitionRepository
            .GetByInstanceIdAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTransition> { t1, t2 });

        var input = new GetInstanceHistoryInput
        {
            Domain = TestDomain,
            Workflow = TestWorkflow,
            Instance = instance.Id.ToString()
        };

        var result = await _service.GetInstanceHistoryAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var output = result.Value!;
        output.Transitions.Count.ShouldBe(2);

        var first = output.Transitions[0];
        first.Id.ShouldBe(t1.Id);
        first.TransitionId.ShouldBe("approve");
        first.FromState.ShouldBe("draft");
        first.ToState.ShouldBe("approved");
        first.TriggerType.ShouldBe(TriggerType.Manual);
        first.FinishedAt.ShouldNotBeNull();
        first.DurationSeconds.ShouldNotBeNull();

        var second = output.Transitions[1];
        second.TransitionId.ShouldBe("complete");
        second.FromState.ShouldBe("approved");
        second.ToState.ShouldBeNull();
        second.FinishedAt.ShouldBeNull();
        second.DurationSeconds.ShouldBeNull();
    }

    [Fact]
    public async Task GetInstanceHistoryAsync_WhenNoTransitions_ReturnsEmptyList()
    {
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");

        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);

        _instanceTransitionRepository
            .GetByInstanceIdAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTransition>());

        var input = new GetInstanceHistoryInput
        {
            Domain = TestDomain,
            Workflow = TestWorkflow,
            Instance = instance.Id.ToString()
        };

        var result = await _service.GetInstanceHistoryAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Transitions.ShouldBeEmpty();
    }
}
