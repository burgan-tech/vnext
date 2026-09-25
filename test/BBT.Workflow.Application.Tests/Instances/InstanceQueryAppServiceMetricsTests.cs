using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Aether.Uow;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Gateway;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Extentions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

using BBT.Workflow.Instances.HumanTask;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for the transition/state metrics reads
/// (<see cref="InstanceQueryAppService.GetTransitionMetricsAsync"/> /
/// <see cref="InstanceQueryAppService.GetStateMetricsAsync"/>, vnext-client-sdk-core#60, item B).
/// Pins the two behaviours the flat tasks function cannot do: grouping firings into attempts, and
/// pairing a state's entry and exit transitions into a visit whose tasks are the state's onEntry
/// (from the entering transition) and onExit (from the leaving transition) — hook-separated from the
/// other work those same transitions also ran.
/// </summary>
public class InstanceQueryAppServiceMetricsTests : IDisposable
{
    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";
    private const string TestState = "waiting";

    private readonly IRuntimeInfoProvider _runtimeInfoProvider;
    private readonly IInstanceRepository _instanceRepository;
    private readonly IInstanceTransitionRepository _instanceTransitionRepository;
    private readonly IInstanceTaskRepository _instanceTaskRepository;
    private readonly InstanceQueryAppService _service;
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public InstanceQueryAppServiceMetricsTests()
    {
        _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
        _instanceRepository = Substitute.For<IInstanceRepository>();
        _instanceTransitionRepository = Substitute.For<IInstanceTransitionRepository>();
        _instanceTaskRepository = Substitute.For<IInstanceTaskRepository>();

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
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            instanceIncidentRepository: Substitute.For<IInstanceIncidentRepository>(),
            instanceTaskRepository: _instanceTaskRepository,
            instanceActionRepository: Substitute.For<IInstanceActionRepository>(),
            longPollInteractionGate: Substitute.For<BBT.Workflow.Execution.LongPoll.ILongPollInteractionGate>(),
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
            callerRoleResolver: new DefaultCallerRoleResolver(Substitute.For<ICurrentUser>()),
            paginationLinkGenerator: Substitute.For<BBT.Aether.Application.Pagination.IPaginationLinkGenerator>(),
            instanceFilteringOptions: Options.Create(new InstanceFilteringOptions()),
            humanTaskOptions: Options.Create(new HumanTaskFunctionOptions()),
            attributeIndexCatalog: Substitute.For<IAttributeIndexCatalog>(),
            stateFunctionCache: Substitute.For<Caching.IStateFunctionCache>(),
            dataFunctionCache: Substitute.For<Caching.IDataFunctionCache>(),
            instanceSchemaFunctionCache: Substitute.For<Caching.IInstanceSchemaFunctionCache>(),
            humanTaskFunctionCache: Substitute.For<Caching.IHumanTaskFunctionCache>(),
            descentLimiter: new HumanTask.HumanTaskDescentLimiter(
                Options.Create(new HumanTask.HumanTaskFunctionOptions())),
            logger: Substitute.For<ILogger<InstanceQueryAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    // ---- transition metrics ----------------------------------------------------------------

    [Fact]
    public async Task GetTransitionMetricsAsync_WhenInstanceNotFound_ReturnsFailure()
    {
        var input = TransitionInput(Guid.NewGuid().ToString(), "to-review");
        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(input.Instance, Arg.Any<CancellationToken>())
            .Returns((Instance?)null);

        var result = await _service.GetTransitionMetricsAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        await _instanceTransitionRepository.DidNotReceiveWithAnyArgs()
            .GetByInstanceIdAsReadOnlyAsync(default, default);
    }

    [Fact]
    public async Task GetTransitionMetricsAsync_GroupsEachFiringIntoItsOwnAttempt()
    {
        var instance = SetupInstance();
        var t0 = DateTime.UtcNow.AddMinutes(-10);

        // Two firings of "to-review" (the repo filters by key in SQL); an unrelated task row proves the
        // grouping never attaches a foreign transition's tasks to an attempt.
        var firing1 = Guid.NewGuid();
        var firing2 = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        _instanceTransitionRepository
            .GetByInstanceAndTransitionKeyAsReadOnlyAsync(instance.Id, "to-review", Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTransitionSlim>
            {
                Slim(firing1, instance.Id, "to-review", "step-4", "precheck", t0,
                    duration: TimeSpan.FromMilliseconds(1104), createdBy: "alice"),
                Slim(firing2, instance.Id, "to-review", "step-4", "precheck", t0.AddSeconds(10),
                    duration: TimeSpan.FromMilliseconds(1098), createdBy: "carol")
            });

        _instanceTaskRepository
            .GetMetricsRowsByTransitionIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTaskMetricsRow>
            {
                // firing 1: a serial onExecute then a parallel pair
                TaskRow(firing1, "ns-mock-risk-recalc", TaskTrigger.OnExecute, 1, t0.AddMilliseconds(1), 511),
                TaskRow(firing1, "ns-mock-crm-enrich", TaskTrigger.OnExecute, 2, t0.AddMilliseconds(520), 237),
                TaskRow(firing1, "ns-mock-doc-scan", TaskTrigger.OnExecute, 2, t0.AddMilliseconds(520), 344),
                // firing 2: its own set
                TaskRow(firing2, "ns-mock-risk-recalc", TaskTrigger.OnExecute, 1, t0.AddSeconds(10).AddMilliseconds(1), 518),
                // unrelated firing's task must never appear
                TaskRow(unrelated, "ns-mock-noise", TaskTrigger.OnExecute, 1, t0.AddSeconds(5), 10)
            });

        var result = await _service.GetTransitionMetricsAsync(
            TransitionInput(instance.Id.ToString(), "to-review"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var output = result.Value!;
        output.Element.Kind.ShouldBe("transition");
        output.Element.Key.ShouldBe("to-review");
        output.Count.ShouldBe(2);
        output.Attempts.Count.ShouldBe(2);

        var attempt1 = output.Attempts[0];
        attempt1.Seq.ShouldBe(1);
        attempt1.DurationMs.ShouldBe(1104);
        attempt1.TriggeredBy.ShouldBe("alice");
        attempt1.Tasks.Count.ShouldBe(3);
        // execution order: risk-recalc (order 1) before the two order-2 siblings
        attempt1.Tasks[0].TaskKey.ShouldBe("ns-mock-risk-recalc");
        attempt1.Tasks[0].Hook.ShouldBe(TaskTrigger.OnExecute);
        attempt1.Tasks.Select(t => t.TaskKey).ShouldContain("ns-mock-doc-scan");
        attempt1.Tasks.ShouldNotContain(t => t.TaskKey == "ns-mock-noise");

        var attempt2 = output.Attempts[1];
        attempt2.Seq.ShouldBe(2);
        attempt2.DurationMs.ShouldBe(1098);
        attempt2.TriggeredBy.ShouldBe("carol");
        attempt2.Tasks.Count.ShouldBe(1);
        attempt2.Tasks[0].TaskKey.ShouldBe("ns-mock-risk-recalc");
    }

    [Fact]
    public async Task GetTransitionMetricsAsync_WhenNoFiring_ReturnsEmptyAttempts()
    {
        var instance = SetupInstance();
        _instanceTransitionRepository
            .GetByInstanceAndTransitionKeyAsReadOnlyAsync(instance.Id, "never-fired", Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTransitionSlim>());

        var result = await _service.GetTransitionMetricsAsync(
            TransitionInput(instance.Id.ToString(), "never-fired"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Count.ShouldBe(0);
        result.Value!.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTransitionMetricsAsync_SurfacesFaultReason()
    {
        var instance = SetupInstance();
        var firing = Guid.NewGuid();
        _instanceTransitionRepository
            .GetByInstanceAndTransitionKeyAsReadOnlyAsync(instance.Id, "approve", Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTransitionSlim>
            {
                Slim(firing, instance.Id, "approve", "draft", "approved", DateTime.UtcNow.AddMinutes(-1))
            });
        _instanceTaskRepository
            .GetMetricsRowsByTransitionIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTaskMetricsRow>
            {
                new(Guid.NewGuid(), firing, "call-api", TaskTrigger.OnExecute, 1,
                    Definitions.TaskStatus.Faulted, BusinessStatus.Failed,
                    DateTime.UtcNow, DateTime.UtcNow, TimeSpan.FromMilliseconds(27),
                    FaultedTaskId: null, FaultedResponseJson: """{"error":"connection refused"}""")
            });

        var result = await _service.GetTransitionMetricsAsync(
            TransitionInput(instance.Id.ToString(), "approve"), CancellationToken.None);

        var task = result.Value!.Attempts[0].Tasks[0];
        task.Status.ShouldBe(Definitions.TaskStatus.Faulted);
        task.Error.ShouldBe("connection refused");
    }

    // ---- state metrics ---------------------------------------------------------------------

    [Fact]
    public async Task GetStateMetricsAsync_PairsEntryAndExitAndHookSplitsTheTasks()
    {
        var instance = SetupInstance();
        var t0 = DateTime.UtcNow.AddMinutes(-10);

        // Enter "precheck" via to-review (from step-4), then leave it via precheck-done (to done).
        var entering = Guid.NewGuid();
        var leaving = Guid.NewGuid();
        _instanceTransitionRepository
            .GetByInstanceIdAsReadOnlyAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTransitionSlim>
            {
                Slim(entering, instance.Id, "to-review", "step-4", "precheck", t0,
                    finishedAt: t0.AddMilliseconds(1100), duration: TimeSpan.FromMilliseconds(1100), createdBy: "alice"),
                Slim(leaving, instance.Id, "precheck-done", "precheck", "done", t0.AddSeconds(30),
                    finishedAt: t0.AddSeconds(30).AddMilliseconds(50), duration: TimeSpan.FromMilliseconds(50))
            });

        _instanceTaskRepository
            .GetMetricsRowsByTransitionIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTaskMetricsRow>
            {
                // under the entering transition: its own onExecute (must be EXCLUDED for a state view),
                // plus precheck's onEntry (must be INCLUDED)
                TaskRow(entering, "ns-mock-risk-recalc", TaskTrigger.OnExecute, 1, t0.AddMilliseconds(1), 500),
                TaskRow(entering, "ns-mock-crm-enrich", TaskTrigger.OnEntry, 1, t0.AddMilliseconds(1101), 200),
                // under the leaving transition: precheck's onExit (INCLUDED), plus the leaving
                // transition's own onExecute (EXCLUDED)
                TaskRow(leaving, "ns-mock-audit", TaskTrigger.OnExit, 1, t0.AddSeconds(30).AddMilliseconds(1), 30),
                TaskRow(leaving, "ns-mock-noise", TaskTrigger.OnExecute, 1, t0.AddSeconds(30).AddMilliseconds(2), 5)
            });

        var result = await _service.GetStateMetricsAsync(
            StateInput(instance.Id.ToString(), "precheck"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var output = result.Value!;
        output.Element.Kind.ShouldBe("state");
        output.Element.Key.ShouldBe("precheck");
        output.Count.ShouldBe(1);

        var visit = output.Attempts[0];
        visit.Seq.ShouldBe(1);
        // entered when the entering transition finished; left when the leaving transition started
        visit.StartedAt.ShouldBe(t0.AddMilliseconds(1100));
        visit.FinishedAt.ShouldBe(t0.AddSeconds(30));
        visit.DurationMs!.Value.ShouldBe((t0.AddSeconds(30) - t0.AddMilliseconds(1100)).TotalMilliseconds, 0.001);
        visit.TriggeredBy.ShouldBe("alice"); // the transition that entered the state

        // onEntry + onExit only — the two onExecute tasks under those records are excluded
        var keys = visit.Tasks.Select(t => t.TaskKey).ToList();
        keys.ShouldBe(new[] { "ns-mock-crm-enrich", "ns-mock-audit" });
        visit.Tasks[0].Hook.ShouldBe(TaskTrigger.OnEntry);
        visit.Tasks[1].Hook.ShouldBe(TaskTrigger.OnExit);
    }

    [Fact]
    public async Task GetStateMetricsAsync_StillInState_ProducesHalfOpenVisit()
    {
        var instance = SetupInstance();
        var t0 = DateTime.UtcNow.AddMinutes(-2);
        var entering = Guid.NewGuid();

        _instanceTransitionRepository
            .GetByInstanceIdAsReadOnlyAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTransitionSlim>
            {
                Slim(entering, instance.Id, "to-review", "step-4", "precheck", t0,
                    finishedAt: t0.AddMilliseconds(900), duration: TimeSpan.FromMilliseconds(900))
                // no leaving record: still sitting in precheck
            });
        _instanceTaskRepository
            .GetMetricsRowsByTransitionIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTaskMetricsRow>
            {
                TaskRow(entering, "ns-mock-crm-enrich", TaskTrigger.OnEntry, 1, t0.AddMilliseconds(901), 200)
            });

        var result = await _service.GetStateMetricsAsync(
            StateInput(instance.Id.ToString(), "precheck"), CancellationToken.None);

        var visit = result.Value!.Attempts.ShouldHaveSingleItem();
        visit.FinishedAt.ShouldBeNull();
        visit.DurationMs.ShouldBeNull();
        visit.Tasks.ShouldHaveSingleItem().TaskKey.ShouldBe("ns-mock-crm-enrich");
    }

    [Fact]
    public async Task GetStateMetricsAsync_CountsEachEntryAsASeparateVisit()
    {
        var instance = SetupInstance();
        var t0 = DateTime.UtcNow.AddMinutes(-20);
        var enter1 = Guid.NewGuid();
        var leave1 = Guid.NewGuid();
        var enter2 = Guid.NewGuid();
        var leave2 = Guid.NewGuid();

        _instanceTransitionRepository
            .GetByInstanceIdAsReadOnlyAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTransitionSlim>
            {
                Slim(enter1, instance.Id, "to-review", "step-4", "precheck", t0, finishedAt: t0.AddMilliseconds(10)),
                Slim(leave1, instance.Id, "reject", "precheck", "step-4", t0.AddSeconds(5)),
                Slim(enter2, instance.Id, "to-review", "step-4", "precheck", t0.AddSeconds(10), finishedAt: t0.AddSeconds(10).AddMilliseconds(10)),
                Slim(leave2, instance.Id, "approve", "precheck", "done", t0.AddSeconds(15))
            });
        _instanceTaskRepository
            .GetMetricsRowsByTransitionIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTaskMetricsRow>());

        var result = await _service.GetStateMetricsAsync(
            StateInput(instance.Id.ToString(), "precheck"), CancellationToken.None);

        result.Value!.Count.ShouldBe(2);
        result.Value!.Attempts.Select(a => a.Seq).ShouldBe(new[] { 1, 2 });
    }

    [Fact]
    public async Task GetStateMetricsAsync_SelfLoop_ClosesOneVisitAndOpensTheNextInOneRecord()
    {
        var instance = SetupInstance();
        var t0 = DateTime.UtcNow.AddMinutes(-20);
        var enter = Guid.NewGuid();
        var selfLoop = Guid.NewGuid();
        var leave = Guid.NewGuid();

        _instanceTransitionRepository
            .GetByInstanceIdAsReadOnlyAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTransitionSlim>
            {
                Slim(enter, instance.Id, "to-review", "step-4", "precheck", t0, finishedAt: t0.AddMilliseconds(10)),
                // self-loop: leaves precheck AND re-enters it in one record
                Slim(selfLoop, instance.Id, "recheck", "precheck", "precheck", t0.AddSeconds(5), finishedAt: t0.AddSeconds(5).AddMilliseconds(10)),
                Slim(leave, instance.Id, "approve", "precheck", "done", t0.AddSeconds(10))
            });
        _instanceTaskRepository
            .GetMetricsRowsByTransitionIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTaskMetricsRow>
            {
                // the self-loop record carries BOTH precheck's onExit (belongs to visit 1, which it closes)
                // and its onEntry (belongs to visit 2, which it opens)
                TaskRow(selfLoop, "audit-on-exit", TaskTrigger.OnExit, 1, t0.AddSeconds(5).AddMilliseconds(1), 5),
                TaskRow(selfLoop, "notify-on-entry", TaskTrigger.OnEntry, 1, t0.AddSeconds(5).AddMilliseconds(6), 5)
            });

        var result = await _service.GetStateMetricsAsync(
            StateInput(instance.Id.ToString(), "precheck"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Count.ShouldBe(2);

        var visit1 = result.Value!.Attempts[0];
        visit1.FinishedAt.ShouldBe(t0.AddSeconds(5)); // closed by the self-loop record's StartedAt
        visit1.Tasks.ShouldHaveSingleItem().TaskKey.ShouldBe("audit-on-exit");

        var visit2 = result.Value!.Attempts[1];
        visit2.StartedAt.ShouldBe(t0.AddSeconds(5).AddMilliseconds(10)); // opened by the same record's FinishedAt
        visit2.FinishedAt.ShouldBe(t0.AddSeconds(10)); // left by 'approve'
        visit2.Tasks.ShouldHaveSingleItem().TaskKey.ShouldBe("notify-on-entry");
    }

    [Fact]
    public async Task GetStateMetricsAsync_OmitsLegacyNullHookTasksFromThePhaseSplit()
    {
        var instance = SetupInstance();
        var t0 = DateTime.UtcNow.AddMinutes(-3);
        var entering = Guid.NewGuid();
        var leaving = Guid.NewGuid();

        _instanceTransitionRepository
            .GetByInstanceIdAsReadOnlyAsync(instance.Id, Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTransitionSlim>
            {
                Slim(entering, instance.Id, "to-review", "step-4", "precheck", t0, finishedAt: t0.AddMilliseconds(10)),
                Slim(leaving, instance.Id, "approve", "precheck", "done", t0.AddSeconds(5))
            });
        _instanceTaskRepository
            .GetMetricsRowsByTransitionIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<InstanceTaskMetricsRow>
            {
                // legacy row (null hook) cannot be classified into onEntry/onExit — omitted
                TaskRow(entering, "legacy-task", null, null, t0.AddMilliseconds(11), 100),
                TaskRow(entering, "new-entry", TaskTrigger.OnEntry, 1, t0.AddMilliseconds(12), 100)
            });

        var result = await _service.GetStateMetricsAsync(
            StateInput(instance.Id.ToString(), "precheck"), CancellationToken.None);

        var visit = result.Value!.Attempts.ShouldHaveSingleItem();
        visit.Tasks.ShouldHaveSingleItem().TaskKey.ShouldBe("new-entry");
    }

    // ---- helpers ---------------------------------------------------------------------------

    private Instance SetupInstance()
    {
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "test-key");
        var state = State.Create(TestState, StateType.Intermediate, StateSubType.None,
            VersionStrategy.IncreaseMinor.Code);
        instance.ChangeState(state);

        _instanceRepository
            .FindByIdentifierAsReadOnlyAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);

        return instance;
    }

    private static InstanceTransitionSlim Slim(
        Guid id, Guid instanceId, string transitionKey, string fromState, string? toState,
        DateTime startedAt, DateTime? finishedAt = null, TimeSpan? duration = null,
        TriggerType triggerType = TriggerType.Manual, string? createdBy = null) =>
        new(id, instanceId, transitionKey, fromState, toState, startedAt, finishedAt, duration,
            triggerType, startedAt, createdBy, null);

    private static InstanceTaskMetricsRow TaskRow(
        Guid transitionId, string taskKey, TaskTrigger? hook, int? order,
        DateTime startedAt, double durationMs) =>
        new(Guid.NewGuid(), transitionId, taskKey, hook, order,
            Definitions.TaskStatus.Completed, BusinessStatus.Success,
            startedAt, startedAt.AddMilliseconds(durationMs), TimeSpan.FromMilliseconds(durationMs),
            FaultedTaskId: null, FaultedResponseJson: null);

    private static GetTransitionMetricsInput TransitionInput(string instance, string transitionKey) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = instance,
        TransitionKey = transitionKey
    };

    private static GetStateMetricsInput StateInput(string instance, string stateKey) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = instance,
        StateKey = stateKey
    };
}
