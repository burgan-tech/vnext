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
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins that the start answer carries the flow version the runtime RESOLVED (a floating request such as
/// "1.0" lands on the newest 1.0.x), so a caller — a parent's SubFlow correlation — can remember the
/// child's real version instead of the authored reference. An idempotent hit answers with the existing
/// instance's own version.
/// </summary>
public class InstanceCommandAppServiceStartFlowVersionTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string Flow = "test-flow";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IWorkflowExecutionService _execution = Substitute.For<IWorkflowExecutionService>();
    private readonly InstanceCommandAppService _service;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;

    public InstanceCommandAppServiceStartFlowVersionTests()
    {
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        mockUoWManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(Substitute.For<IUnitOfWork>());
        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        // PrepareInstanceAsync opens its unit of work through ApplicationService.UnitOfWorkManager.
        services.AddSingleton<ILazyServiceProvider>(sp => new LazyServiceProvider(sp));
        _ambient = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        // The backend holds 1.0.0 and 1.0.3; a floating "1.0" resolves to the newest, 1.0.3.
        var resolved = Definitions.Workflow.Create();
        resolved.SetReference(new Reference(Flow, Domain, "sys-flows", "1.0.3"));
        resolved.SetType("F");
        resolved.SetStartTransition(Transition.Create("start", null, "s1", TriggerType.Manual, "Patch"));
        _componentCacheStore.GetFlowAsync(Domain, Flow, "1.0", Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(resolved));

        var validation = Substitute.For<ITransitionValidationService>();
        validation.ValidateStartTransitionAsync(default!, default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(Result.Ok());
        _instanceRepository.InsertAsync(default!, default, default)
            .ReturnsForAnyArgs(ci => ci.Arg<Instance>());
        _execution.ExecuteTransitionAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = Guid.NewGuid(), Status = InstanceStatus.Active }));

        _service = new InstanceCommandAppService(
            serviceProvider: _ambient,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            workflowExecutionService: _execution,
            componentCacheStore: _componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceDataWriteService: Substitute.For<IInstanceDataWriteService>(),
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            backgroundJobService: Substitute.For<IBackgroundJobService>(),
            guidGenerator: Substitute.For<IGuidGenerator>(),
            headerService: Substitute.For<IHeaderService>(),
            transitionDataMapper: Substitute.For<ITransitionDataMapper>(),
            transitionValidationService: validation,
            transitionAdmissionService: Substitute.For<ITransitionAdmissionService>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            instanceDataReadService: new BBT.Workflow.Instances.InstanceDataReadService(Substitute.For<ISchemaFieldFilterService>()),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            timerEvaluator: Substitute.For<ITimerEvaluator>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            cancellationService: Substitute.For<IInstanceCancellationService>(),
            longPollAckResumeService: Substitute.For<ILongPollAckResumeService>(),
            instanceCommandGateway: Substitute.For<IInstanceCommandGateway>(),
            workflowOutputMappingService: Substitute.For<IWorkflowOutputMappingService>(),
            fileOffloadService: BBT.Workflow.Application.Files.FileTestDoubles.PassThroughOffload(),
            rawBodyProvider: Substitute.For<BBT.Workflow.Scripting.IRequestRawBodyProvider>(),
            subflowProxyService: Substitute.For<BBT.Workflow.SubFlow.ISubflowProxyService>(),
            logger: Substitute.For<ILogger<InstanceCommandAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        (_ambient as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task StartAsync_ShouldReturnResolvedFlowVersion_WhenRequestIsFloating()
    {
        var result = await _service.StartAsync(CreateInput("1.0", key: "k-" + Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.FlowVersion.ShouldBe("1.0.3");
    }

    [Fact]
    public async Task StartAsync_ShouldReturnExistingInstanceFlowVersion_WhenIdempotentHit()
    {
        var key = "k-" + Guid.NewGuid();
        _instanceRepository.FindActiveByKeyLeanAsync(key, Arg.Any<CancellationToken>())
            .Returns(Instance.Create(Guid.NewGuid(), Flow, "1.0.0", key));

        var result = await _service.StartAsync(CreateInput("1.0", key, strict: false), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.FlowVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task StartAsync_ShouldReturnFlowVersion_WhenResponseEnrichmentSuppressed()
    {
        var input = CreateInput("1.0", key: "k-" + Guid.NewGuid());
        input.SuppressResponseEnrichment = true;

        var result = await _service.StartAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.FlowVersion.ShouldBe("1.0.3");
    }

    private static StartInstanceInput CreateInput(string version, string key, bool strict = true)
        => new(Domain, Flow, version)
        {
            Instance = new CreateInstanceInput { Key = key },
            StrictIdempotency = strict
        };
}
