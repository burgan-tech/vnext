using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.LongPoll;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Extentions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Headers;
using BBT.Workflow.Logging;
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
/// Unit tests for the light Busy fast-fail in <see cref="InstanceCommandAppService.TransitionAsync"/>:
/// a Busy instance is rejected from the single-row execution snapshot BEFORE the full aggregate
/// (DataList + correlations) is loaded; exempt kinds and Busy-with-active-subflow fall through to
/// the full path.
/// </summary>
public class InstanceCommandAppServiceBusyFastFailTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string Flow = "test-flow";
    private const string Version = "1.0.0";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly ITransitionAdmissionService _admissionService = Substitute.For<ITransitionAdmissionService>();
    private readonly IWorkflowExecutionService _executionService = Substitute.For<IWorkflowExecutionService>();
    private readonly InstanceCommandAppService _service;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;

    public InstanceCommandAppServiceBusyFastFailTests()
    {
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        _ambient = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        _componentCacheStore.GetFlowAsync(Domain, Flow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(CreateWorkflow()));

        _service = new InstanceCommandAppService(
            serviceProvider: _ambient,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            workflowExecutionService: _executionService,
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceDataWriteService: Substitute.For<IInstanceDataWriteService>(),
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            backgroundJobService: Substitute.For<IBackgroundJobService>(),
            guidGenerator: Substitute.For<IGuidGenerator>(),
            headerService: Substitute.For<IHeaderService>(),
            transitionDataMapper: Substitute.For<ITransitionDataMapper>(),
            transitionValidationService: Substitute.For<ITransitionValidationService>(),
            transitionAdmissionService: _admissionService,
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            instanceExtensionService: Substitute.For<IInstanceExtensionService>(),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            timerEvaluator: Substitute.For<ITimerEvaluator>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            cancellationService: Substitute.For<IInstanceCancellationService>(),
            longPollAckResumeService: Substitute.For<ILongPollAckResumeService>(),
            instanceCommandGateway: Substitute.For<IInstanceCommandGateway>(),
            workflowOutputMappingService: Substitute.For<IWorkflowOutputMappingService>(),
            currentUser: Substitute.For<ICurrentUser>(),
            logger: Substitute.For<ILogger<InstanceCommandAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        (_ambient as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task TransitionAsync_BusySnapshot_Returns409WithoutLoadingAggregate()
    {
        var instanceId = Guid.NewGuid();
        SetupSnapshot(instanceId, InstanceStatus.Busy, hasActiveSubFlow: false);
        _admissionService
            .ClassifyKey(Arg.Any<Definitions.Workflow>(), "regular-transition")
            .Returns(AdmissionKind.Normal);

        var result = await _service.TransitionAsync(
            instanceId.ToString(), "regular-transition", CreateInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);

        // The whole point: the full aggregate (DataList + correlations) is never loaded.
        await _instanceRepository.DidNotReceive()
            .GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_BusyWithActiveSubflow_FallsThroughToFullPath()
    {
        // Forward adayı — fast-fail devreye girmez; tam yükleme yapılır (pipeline forward eder).
        var instanceId = Guid.NewGuid();
        SetupSnapshot(instanceId, InstanceStatus.Busy, hasActiveSubFlow: true);
        _instanceRepository.GetActiveAsync(instanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Fail(WorkflowErrors.InstanceNotFound(instanceId.ToString())));

        await _service.TransitionAsync(
            instanceId.ToString(), "regular-transition", CreateInput(), CancellationToken.None);

        await _instanceRepository.Received(1)
            .GetActiveAsync(instanceId.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_BusyWithExemptKind_FallsThroughToFullPath()
    {
        // cancel/exit/updateData Busy'de fast-fail'e takılmaz.
        var instanceId = Guid.NewGuid();
        SetupSnapshot(instanceId, InstanceStatus.Busy, hasActiveSubFlow: false);
        _admissionService
            .ClassifyKey(Arg.Any<Definitions.Workflow>(), "cancel")
            .Returns(AdmissionKind.BypassBusyCheck);
        _instanceRepository.GetActiveAsync(instanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Fail(WorkflowErrors.InstanceNotFound(instanceId.ToString())));

        await _service.TransitionAsync(
            instanceId.ToString(), "cancel", CreateInput(), CancellationToken.None);

        await _instanceRepository.Received(1)
            .GetActiveAsync(instanceId.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_ActiveSnapshot_FallsThroughToFullPath()
    {
        var instanceId = Guid.NewGuid();
        SetupSnapshot(instanceId, InstanceStatus.Active, hasActiveSubFlow: false);
        _instanceRepository.GetActiveAsync(instanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Fail(WorkflowErrors.InstanceNotFound(instanceId.ToString())));

        await _service.TransitionAsync(
            instanceId.ToString(), "regular-transition", CreateInput(), CancellationToken.None);

        await _instanceRepository.Received(1)
            .GetActiveAsync(instanceId.ToString(), Arg.Any<CancellationToken>());
        _admissionService.DidNotReceive()
            .ClassifyKey(Arg.Any<Definitions.Workflow>(), Arg.Any<string>());
    }

    [Fact]
    public async Task TransitionAsync_BusyLeafWithChainReservedClaim_FallsThroughToFullPath()
    {
        // Relay to the leaf: the accept already flipped this instance Busy as part of its SubFlow
        // chain reserve, so the Busy the fast-fail sees is the relay's own. Without the exemption
        // the forward 409s and the flow deadlocks at the leaf.
        var instanceId = Guid.NewGuid();
        SetupSnapshot(instanceId, InstanceStatus.Busy, hasActiveSubFlow: false);
        _admissionService
            .ClassifyKey(Arg.Any<Definitions.Workflow>(), "regular-transition")
            .Returns(AdmissionKind.Normal);
        _instanceRepository.GetActiveAsync(instanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Fail(WorkflowErrors.InstanceNotFound(instanceId.ToString())));

        var input = CreateInput();
        input.ChainReserved = true;

        var result = await _service.TransitionAsync(
            instanceId.ToString(), "regular-transition", input, CancellationToken.None);

        result.Error.Code.ShouldNotBe(WorkflowErrorCodes.InstanceBusy);
        await _instanceRepository.Received(1)
            .GetActiveAsync(instanceId.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_BusyLeafWithoutClaim_StillReturns409()
    {
        // The claim is the only thing that opens this door — a leaf Busy for its own reasons must
        // still reject, or the Busy-as-mutex guarantee is gone.
        var instanceId = Guid.NewGuid();
        SetupSnapshot(instanceId, InstanceStatus.Busy, hasActiveSubFlow: false);
        _admissionService
            .ClassifyKey(Arg.Any<Definitions.Workflow>(), "regular-transition")
            .Returns(AdmissionKind.Normal);

        var input = CreateInput();
        input.ChainReserved = false;

        var result = await _service.TransitionAsync(
            instanceId.ToString(), "regular-transition", input, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);
    }

    private void SetupSnapshot(Guid instanceId, InstanceStatus status, bool hasActiveSubFlow)
        => _instanceRepository
            .GetExecutionSnapshotAsync(instanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new InstanceExecutionSnapshot(
                instanceId, "key", status, "state1", Flow, Version, hasActiveSubFlow));

    private static TransitionInput CreateInput()
        => new(Domain, Flow, new TransitionDataInput(null), sync: true)
        {
            Headers = new Dictionary<string, string?>(),
            RouteValues = new Dictionary<string, string?>()
        };

    private static Definitions.Workflow CreateWorkflow()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(Flow, Domain, "sys-flows", Version));
        return workflow;
    }
}
