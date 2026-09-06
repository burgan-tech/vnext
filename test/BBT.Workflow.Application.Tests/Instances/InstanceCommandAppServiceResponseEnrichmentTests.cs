using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
/// The sync response projection (<c>EnrichOutputCoreAsync</c>): it is one attributable phase
/// (<c>Instance.EnrichResponse</c>), and runtime-internal child calls — sub-start and subflow
/// forward — skip it entirely because nothing reads what it produces.
/// </summary>
public class InstanceCommandAppServiceResponseEnrichmentTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string Flow = "test-flow";
    private const string Version = "1.0.0";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IWorkflowExecutionService _executionService = Substitute.For<IWorkflowExecutionService>();
    private readonly IInstanceExtensionService _extensionService = Substitute.For<IInstanceExtensionService>();
    private readonly InstanceCommandAppService _service;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;

    public InstanceCommandAppServiceResponseEnrichmentTests()
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
            transitionAdmissionService: Substitute.For<ITransitionAdmissionService>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            schemaFieldFilterService: Substitute.For<ISchemaFieldFilterService>(),
            instanceExtensionService: _extensionService,
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            timerEvaluator: Substitute.For<ITimerEvaluator>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            cancellationService: Substitute.For<IInstanceCancellationService>(),
            longPollAckResumeService: Substitute.For<ILongPollAckResumeService>(),
            instanceCommandGateway: Substitute.For<IInstanceCommandGateway>(),
            workflowOutputMappingService: Substitute.For<IWorkflowOutputMappingService>(),
            callerRoleResolver: new DefaultCallerRoleResolver(Substitute.For<ICurrentUser>()),
            logger: Substitute.For<ILogger<InstanceCommandAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        (_ambient as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task TransitionAsync_SyncRequest_ProjectsTheResponseUnderAnEnrichSpan()
    {
        using var root = StartRoot();
        var collected = Listen(root);
        var instanceId = Guid.NewGuid();
        SetupActiveSnapshot(instanceId);
        SetupExecution(instanceId);
        // No PipelineInstance on the output and no row on the read-only reload: the projection
        // takes its early exit, which is enough to pin the span and its source tag.
        _instanceRepository.FindByIdentifierAsReadOnlyAsync(instanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns((Instance?)null);

        var result = await _service.TransitionAsync(
            instanceId.ToString(), "regular-transition", CreateInput(sync: true), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var enrich = collected.Single(a => a.DisplayName == "Instance.EnrichResponse");
        enrich.GetTagItem(TelemetryConstants.TagNames.InstanceId).ShouldBe(instanceId.ToString());
        enrich.GetTagItem(TelemetryConstants.TagNames.Flow).ShouldBe(Flow);
        enrich.GetTagItem(TelemetryConstants.TagNames.EnrichSource).ShouldBe("reload");
        await _instanceRepository.Received(1)
            .FindByIdentifierAsReadOnlyAsync(instanceId.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionAsync_SyncRequestWithSuppression_SkipsTheProjectionEntirely()
    {
        using var root = StartRoot();
        var collected = Listen(root);
        var instanceId = Guid.NewGuid();
        SetupActiveSnapshot(instanceId);
        SetupExecution(instanceId);
        var input = CreateInput(sync: true);
        input.SuppressResponseEnrichment = true;

        var result = await _service.TransitionAsync(
            instanceId.ToString(), "regular-transition", input, CancellationToken.None);

        // Still a sync call — the caller awaited the pipeline — but the response is identity-only.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Id.ShouldBe(instanceId);
        result.Value.Key.ShouldBe("key");
        result.Value.Status.ShouldBe(InstanceStatus.Active);
        result.Value.Attributes.ShouldBeNull();
        result.Value.Extensions.ShouldBeNull();
        collected.ShouldNotContain(a => a.DisplayName == "Instance.EnrichResponse");
        await _instanceRepository.DidNotReceiveWithAnyArgs()
            .FindByIdentifierAsReadOnlyAsync(default!, default);
        await _extensionService.DidNotReceiveWithAnyArgs()
            .ProcessExtensionsAsync(default, default!, default!, default, default);
    }

    [Fact]
    public async Task TransitionAsync_AsyncRequest_NeverProjects()
    {
        using var root = StartRoot();
        var collected = Listen(root);
        var instanceId = Guid.NewGuid();
        SetupActiveSnapshot(instanceId);
        SetupExecution(instanceId);

        var result = await _service.TransitionAsync(
            instanceId.ToString(), "regular-transition", CreateInput(sync: false), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        collected.ShouldNotContain(a => a.DisplayName == "Instance.EnrichResponse");
        await _instanceRepository.DidNotReceiveWithAnyArgs()
            .FindByIdentifierAsReadOnlyAsync(default!, default);
    }

    private void SetupActiveSnapshot(Guid instanceId)
        => _instanceRepository
            .GetExecutionSnapshotAsync(instanceId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new InstanceExecutionSnapshot(
                instanceId, "key", InstanceStatus.Active, "state1", Flow, Version, false));

    private void SetupExecution(Guid instanceId)
        => _executionService
            .ExecuteTransitionAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = instanceId,
                Status = InstanceStatus.Active
            }));

    private static Activity StartRoot()
    {
        var root = new Activity("enrich-test-root");
        root.SetIdFormat(ActivityIdFormat.W3C);
        root.Start();
        return root;
    }

    private static List<Activity> Listen(Activity root)
    {
        var collected = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.Pipeline",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.TraceId != root.TraceId) return;
                lock (collected) collected.Add(a);
            }
        };
        ActivitySource.AddActivityListener(listener);
        return collected;
    }

    private static TransitionInput CreateInput(bool sync)
        => new(Domain, Flow, new TransitionDataInput(null), sync)
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
