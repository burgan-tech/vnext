using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.LongPoll;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Gateway;
using BBT.Workflow.Headers;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins the effective-execution-mode WIRING (vnext#1003) in
/// <see cref="InstanceCommandAppService.TransitionAsync"/>: the transition context's <c>Mode</c> (which
/// the strategy factory keys on) is resolved from the definition's <c>executionType</c> and overrides the
/// caller's <c>sync</c> query parameter, while <c>CallerMode</c> keeps what the caller asked for. The
/// precedence matrix itself is covered by <c>ExecutionModeResolverTests</c>; this proves the resolver is
/// actually invoked at the call site with the definition's value and its result lands on the context.
/// </summary>
public sealed class InstanceCommandAppServiceExecutionTypeTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string Flow = "test-flow";
    private const string Version = "1.0.0";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IWorkflowExecutionService _workflowExecutionService = Substitute.For<IWorkflowExecutionService>();
    private readonly InstanceCommandAppService _service;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;
    private WorkflowExecutionContext? _captured;

    public InstanceCommandAppServiceExecutionTypeTests()
    {
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        _ambient = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        // Capture the context the app service hands to execution; return a benign sync output.
        _workflowExecutionService
            .ExecuteTransitionAsync(Arg.Do<WorkflowExecutionContext>(c => _captured = c), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = Guid.NewGuid(), Status = InstanceStatus.Active }));

        _service = new InstanceCommandAppService(
            serviceProvider: _ambient,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            workflowExecutionService: _workflowExecutionService,
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceDataWriteService: Substitute.For<IInstanceDataWriteService>(),
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            backgroundJobService: Substitute.For<IBackgroundJobService>(),
            guidGenerator: Substitute.For<IGuidGenerator>(),
            headerService: Substitute.For<IHeaderService>(),
            transitionDataMapper: Substitute.For<ITransitionDataMapper>(),
            transitionValidationService: Substitute.For<ITransitionValidationService>(),
            transitionAdmissionService: Substitute.For<ITransitionAdmissionService>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            timerEvaluator: Substitute.For<ITimerEvaluator>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            cancellationService: Substitute.For<IInstanceCancellationService>(),
            longPollAckResumeService: Substitute.For<ILongPollAckResumeService>(),
            instanceCommandGateway: Substitute.For<IInstanceCommandGateway>(),
            workflowOutputMappingService: Substitute.For<IWorkflowOutputMappingService>(),
            logger: Substitute.For<ILogger<InstanceCommandAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        (_ambient as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task TransitionAsync_TransitionExecutionTypeSync_OverridesAsyncCaller()
    {
        // Definition says SYNC; caller sent sync=false (async). Effective mode must be Sync (definition
        // wins), while CallerMode records the async the caller asked for.
        SetupInstanceAndWorkflow(WithSharedTransition(ExecutionType.Sync));

        await _service.TransitionAsync(Guid.NewGuid().ToString(), "go", Input(sync: false), CancellationToken.None);

        _captured.ShouldNotBeNull();
        _captured!.Mode.ShouldBe(ExecMode.Sync);
        _captured.CallerMode.ShouldBe(ExecMode.Async);
    }

    [Fact]
    public async Task TransitionAsync_TransitionExecutionTypeAsync_OverridesSyncCaller()
    {
        SetupInstanceAndWorkflow(WithSharedTransition(ExecutionType.Async));

        await _service.TransitionAsync(Guid.NewGuid().ToString(), "go", Input(sync: true), CancellationToken.None);

        _captured!.Mode.ShouldBe(ExecMode.Async);
        _captured.CallerMode.ShouldBe(ExecMode.Sync);
    }

    [Fact]
    public async Task TransitionAsync_NoDefinition_KeepsCallerMode()
    {
        // No executionType anywhere → the caller's sync query parameter stands (pre-#1003 behaviour).
        SetupInstanceAndWorkflow(WithSharedTransition(executionType: null));

        await _service.TransitionAsync(Guid.NewGuid().ToString(), "go", Input(sync: false), CancellationToken.None);

        _captured!.Mode.ShouldBe(ExecMode.Async);
        _captured.CallerMode.ShouldBe(ExecMode.Async);
    }

    [Fact]
    public async Task TransitionAsync_RuntimeInternalCall_IsNotOverriddenByDefinition()
    {
        // A runtime-internal relay (subflow forward) forces sync=true + SuppressResponseEnrichment so the
        // parent forwards synchronously into the active child. executionType=ASYNC must NOT flip that to
        // async — otherwise the child is only enqueued and the parent proceeds on an unfinished child.
        SetupInstanceAndWorkflow(WithSharedTransition(ExecutionType.Async));

        var input = new TransitionInput(Domain, Flow, null, sync: true) { SuppressResponseEnrichment = true };
        await _service.TransitionAsync(Guid.NewGuid().ToString(), "go", input, CancellationToken.None);

        _captured!.Mode.ShouldBe(ExecMode.Sync);        // forced sync preserved, definition ignored
        _captured.CallerMode.ShouldBe(ExecMode.Sync);
    }

    private void SetupInstanceAndWorkflow(Definitions.Workflow workflow)
    {
        _instanceRepository
            .GetExecutionSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new InstanceExecutionSnapshot(
                Guid.NewGuid(), "k", InstanceStatus.Active, "s1", Flow, Version, HasActiveSubFlow: false));

        _componentCacheStore
            .GetFlowAsync(Domain, Flow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));
    }

    private static Definitions.Workflow WithSharedTransition(ExecutionType? executionType)
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(Flow, Domain, "sys-flows", Version));
        workflow.SetStartTransition(Transition.Create("start", null, "s1", TriggerType.Manual, "Patch"));

        Transition go = executionType is null
            ? Transition.Create("go", null, "s1", TriggerType.Manual, "Patch")
            : DeserializeTransition("go", executionType.Code);
        workflow.AddSharedTransition(go);
        return workflow;
    }

    private static Transition DeserializeTransition(string key, string executionCode)
    {
        var json = $$"""
        {
            "key": "{{key}}", "from": null, "target": "s1", "triggerType": "manual",
            "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [],
            "executionType": "{{executionCode}}"
        }
        """;
        return JsonSerializer.Deserialize<Transition>(json, JsonSerializerConstants.JsonOptions)!;
    }

    private static TransitionInput Input(bool sync) => new(Domain, Flow, null, sync);
}
