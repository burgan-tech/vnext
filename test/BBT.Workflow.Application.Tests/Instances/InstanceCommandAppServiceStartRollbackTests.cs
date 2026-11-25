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
/// Creating the instance is all or nothing: <c>PrepareInstanceAsync</c> commits its transaction only
/// when every creation step succeeded and rolls it back otherwise. Before, a start mapping that failed
/// after the INSERT still committed the instance without its data — a sub-item stayed Busy, and its id
/// or key answered every retry with that empty instance. The start transition runs after that commit,
/// so its own failure keeps the committed instance.
/// </summary>
public sealed class InstanceCommandAppServiceStartRollbackTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string Flow = "start-rollback-flow";
    private const string Version = "1.0.0";

    private static readonly JsonElement Payload = JsonSerializer.SerializeToElement(new { amount = 10 });

    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceDataWriteService _dataWrite = Substitute.For<IInstanceDataWriteService>();
    private readonly ITransitionDataMapper _mapper = Substitute.For<ITransitionDataMapper>();
    private readonly ITransitionValidationService _validation = Substitute.For<ITransitionValidationService>();
    private readonly IWorkflowExecutionService _execution = Substitute.For<IWorkflowExecutionService>();
    private readonly InstanceCommandAppService _service;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;
    private Definitions.Workflow _workflow = CreateWorkflow("F");
    private Instance? _inserted;

    public InstanceCommandAppServiceStartRollbackTests()
    {
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_uow);
        var services = new ServiceCollection();
        services.AddSingleton(uowManager);
        // PrepareInstanceAsync opens its unit of work through ApplicationService.UnitOfWorkManager.
        services.AddSingleton<ILazyServiceProvider>(sp => new LazyServiceProvider(sp));
        _ambient = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        var componentCacheStore = Substitute.For<IComponentCacheStore>();
        componentCacheStore.GetFlowAsync(Domain, Flow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Result<Definitions.Workflow>.Ok(_workflow));
        _validation.ValidateStartTransitionAsync(default!, default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(Result.Ok());
        _instanceRepository.InsertAsync(default!, default, default)
            .ReturnsForAnyArgs(ci =>
            {
                _inserted = ci.Arg<Instance>();
                return _inserted;
            });
        _mapper.MapTransitionDataAsync(default, default, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Result<object?>.Ok(new { amount = 10, mapped = true }));
        _execution.ExecuteTransitionAsync(default!, default)
            .ReturnsForAnyArgs(Result<TransitionOutput>.Ok(
                new TransitionOutput { Id = Guid.NewGuid(), Status = InstanceStatus.Active }));

        _service = new InstanceCommandAppService(
            serviceProvider: _ambient,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            workflowExecutionService: _execution,
            componentCacheStore: componentCacheStore,
            instanceRepository: _instanceRepository,
            instanceDataWriteService: _dataWrite,
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            backgroundJobService: Substitute.For<IBackgroundJobService>(),
            guidGenerator: Substitute.For<IGuidGenerator>(),
            headerService: Substitute.For<IHeaderService>(),
            transitionDataMapper: _mapper,
            transitionValidationService: _validation,
            transitionAdmissionService: Substitute.For<ITransitionAdmissionService>(),
            representationEtagService: Substitute.For<IRepresentationEtagService>(),
            instanceDataReadService: new InstanceDataReadService(Substitute.For<ISchemaFieldFilterService>()),
            scriptContextFactory: Substitute.For<IScriptContextFactory>(),
            timerEvaluator: Substitute.For<ITimerEvaluator>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
            cancellationService: Substitute.For<IInstanceCancellationService>(),
            longPollAckResumeService: Substitute.For<ILongPollAckResumeService>(),
            instanceCommandGateway: Substitute.For<IInstanceCommandGateway>(),
            workflowOutputMappingService: Substitute.For<IWorkflowOutputMappingService>(),
            fileOffloadService: BBT.Workflow.Application.Files.FileTestDoubles.PassThroughOffload(),
            rawBodyProvider: Substitute.For<IRequestRawBodyProvider>(),
            subflowProxyService: Substitute.For<BBT.Workflow.SubFlow.ISubflowProxyService>(),
            logger: Substitute.For<ILogger<InstanceCommandAppService>>());
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        (_ambient as IDisposable)?.Dispose();
    }

    [Theory]
    [InlineData("F")] // a root instance: was left Active in its initial state, without its data
    [InlineData("S")] // a SubFlow child: created Busy, was left Busy (or faulted with its parent)
    public async Task StartMappingFails_RollsTheStartBack_AndCommitsNothing(string flowType)
    {
        _workflow = CreateWorkflow(flowType);
        var mappingError = ExecutionErrors.TransitionMappingFailed(
            "the start mapping found no customer", nameof(InvalidOperationException));
        _mapper.MapTransitionDataAsync(default, default, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Result<object?>.Fail(mappingError));

        var result = await _service.StartAsync(CreateInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(mappingError.Code);
        result.Error.Message.ShouldBe(mappingError.Message);
        // The row was already written inside the start transaction; the rollback is what removes it.
        _inserted.ShouldNotBeNull().IsBusy.ShouldBe(flowType == "S");
        await _uow.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await _dataWrite.DidNotReceiveWithAnyArgs().AppendAsync(default!, default!, default);
        await _execution.DidNotReceiveWithAnyArgs().ExecuteTransitionAsync(default!, default);
    }

    [Theory]
    [InlineData("F")]
    [InlineData("S")]
    public async Task StartSucceeds_CommitsOnce_BeforeTheStartTransitionRuns(string flowType)
    {
        _workflow = CreateWorkflow(flowType);

        var result = await _service.StartAsync(CreateInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _uow.DidNotReceive().RollbackAsync(Arg.Any<CancellationToken>());
        await _dataWrite.ReceivedWithAnyArgs(1).AppendAsync(default!, default!, default);
        // The start transition runs against the committed instance, never an uncommitted one.
        Received.InOrder(() =>
        {
            _ = _uow.CommitAsync(Arg.Any<CancellationToken>());
            _ = _execution.ExecuteTransitionAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>());
        });
    }

    /// <summary>
    /// The other side of the boundary: the start transition runs after the commit, so its own failure
    /// keeps the committed instance. Nothing is rolled back.
    /// </summary>
    [Fact]
    public async Task StartTransitionFails_KeepsTheCommittedInstance()
    {
        var pipelineError = Error.Failure("Execution:200001", "the start transition's task failed");
        _execution.ExecuteTransitionAsync(default!, default)
            .ReturnsForAnyArgs(Result<TransitionOutput>.Fail(pipelineError));

        var result = await _service.StartAsync(CreateInput(), CancellationToken.None);

        result.Error.Code.ShouldBe(pipelineError.Code);
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().RollbackAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartValidationFails_RollsBack_BeforeAnyInsert()
    {
        var invalid = Error.Failure("Schema:100001", "Required properties [\"amount\"] are not present");
        _validation.ValidateStartTransitionAsync(default!, default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(Result.Fail(invalid));

        var result = await _service.StartAsync(CreateInput(), CancellationToken.None);

        result.Error.Code.ShouldBe(invalid.Code);
        await _instanceRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default, default);
        await _uow.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartWithoutAttributes_RunsNoMapping_AndCommits()
    {
        var input = CreateInput();
        input.Instance.Attributes = null;

        var result = await _service.StartAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _mapper.DidNotReceiveWithAnyArgs()
            .MapTransitionDataAsync(default, default, default!, default!, default!, default, default, default);
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().RollbackAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>history: none writes no initial data row, but its start mapping still runs and still decides.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoryNoneStart_CommitsOnlyWhenTheStartMappingSucceeds(bool mappingSucceeds)
    {
        _workflow = CreateWorkflow("F");
        _workflow.SetHistory(HistoryMode.None);
        if (!mappingSucceeds)
        {
            _mapper.MapTransitionDataAsync(default, default, default!, default!, default!, default, default, default)
                .ReturnsForAnyArgs(Result<object?>.Fail(
                    ExecutionErrors.TransitionMappingFailed("no customer", nameof(InvalidOperationException))));
        }

        var result = await _service.StartAsync(CreateInput(), CancellationToken.None);

        result.IsSuccess.ShouldBe(mappingSucceeds);
        await _dataWrite.DidNotReceiveWithAnyArgs().AppendAsync(default!, default!, default);
        await _uow.Received(mappingSucceeds ? 1 : 0).CommitAsync(Arg.Any<CancellationToken>());
        await _uow.Received(mappingSucceeds ? 0 : 1).RollbackAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An infrastructure failure throws instead of answering a failed result: it leaves the method without a
    /// commit, and Aether rolls back a unit of work that is disposed uncommitted.
    /// </summary>
    [Fact]
    public async Task InitialDataAppendThrows_PropagatesAndCommitsNothing()
    {
        _dataWrite.AppendAsync(default!, default!, default)
            .ReturnsForAnyArgs<Task<InstanceData?>>(_ => throw new InvalidOperationException("lock timeout"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.StartAsync(CreateInput(), CancellationToken.None));

        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await _uow.Received(1).DisposeAsync();
        await _execution.DidNotReceiveWithAnyArgs().ExecuteTransitionAsync(default!, default);
    }

    private static Definitions.Workflow CreateWorkflow(string flowType)
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(Flow, Domain, "sys-flows", Version));
        workflow.SetType(flowType);
        workflow.SetStartTransition(Transition.Create("start", null, "draft", TriggerType.Manual, "Minor"));
        return workflow;
    }

    private static StartInstanceInput CreateInput()
        => new(Domain, Flow, Version)
        {
            Instance = new CreateInstanceInput { Key = "k-" + Guid.NewGuid(), Attributes = Payload },
            StrictIdempotency = true
        };
}
