using System;
using System.Collections.Generic;
using System.Linq;
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
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Execution.LongPoll;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Files;
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
/// vnext#1268: a start transition whose mapping throws fails the start with the mapper's own error and
/// records no incident, so the log line is where the exception's stack trace goes
/// (<c>StartTransitionMappingFailed</c>, 10269).
/// </summary>
public sealed class InstanceCommandAppServiceStartMappingFailureTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string Flow = "test-flow";
    private const string Version = "1.0.0";

    private readonly ITransitionDataMapper _dataMapper = Substitute.For<ITransitionDataMapper>();
    private readonly CapturingLogger _logger = new();
    private readonly InstanceCommandAppService _service;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;

    public InstanceCommandAppServiceStartMappingFailureTests()
    {
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(Substitute.For<IUnitOfWork>());
        var services = new ServiceCollection();
        services.AddSingleton(uowManager);
        services.AddSingleton<ILazyServiceProvider>(sp => new LazyServiceProvider(sp));
        _ambient = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambient;

        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(Flow, Domain, "sys-flows", Version));
        workflow.SetType("F");
        workflow.SetStartTransition(Transition.Create("start", null, "state1", TriggerType.Manual, "Patch"));
        var componentCacheStore = Substitute.For<IComponentCacheStore>();
        componentCacheStore.GetFlowAsync(Domain, Flow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));

        var validation = Substitute.For<ITransitionValidationService>();
        validation.ValidateStartTransitionAsync(default!, default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(Result.Ok());
        var offload = Substitute.For<IFileOffloadService>();
        offload.GetFieldsAsync(default!, default).ReturnsForAnyArgs(Result<IReadOnlyList<FileStorageField>>.Ok([]));
        var instanceRepository = Substitute.For<IInstanceRepository>();
        instanceRepository.InsertAsync(default!, default, default).ReturnsForAnyArgs(ci => ci.Arg<Instance>());

        _service = new InstanceCommandAppService(
            serviceProvider: _ambient,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            workflowExecutionService: Substitute.For<IWorkflowExecutionService>(),
            componentCacheStore: componentCacheStore,
            instanceRepository: instanceRepository,
            instanceDataWriteService: Substitute.For<IInstanceDataWriteService>(),
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            backgroundJobService: Substitute.For<IBackgroundJobService>(),
            guidGenerator: Substitute.For<IGuidGenerator>(),
            headerService: Substitute.For<IHeaderService>(),
            transitionDataMapper: _dataMapper,
            transitionValidationService: validation,
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
            fileOffloadService: offload,
            rawBodyProvider: Substitute.For<IRequestRawBodyProvider>(),
            subflowProxyService: Substitute.For<BBT.Workflow.SubFlow.ISubflowProxyService>(),
            logger: _logger);
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        (_ambient as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task StartMappingThatThrows_FailsTheStartAsBefore_AndLogsTheException()
    {
        var thrown = new ArgumentException("the start mapping found no customer");
        var mappingError = Error.Failure(WorkflowErrorCodes.ExecutionStepFailed, "Failed to execute transition mapping: " + thrown.Message);
        _dataMapper.MapTransitionDataAsync(
                Arg.Any<object?>(), Arg.Any<Transition?>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(), Arg.Any<IRuntimeInfoProvider>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<Action<Exception>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<Action<Exception>?>()!.Invoke(thrown);
                return Result<object?>.Fail(mappingError);
            });

        var result = await _service.StartAsync(new StartInstanceInput(Domain, Flow, Version)
        {
            Instance = new CreateInstanceInput
            {
                Key = "k-" + Guid.NewGuid(),
                Attributes = JsonDocument.Parse("""{ "amount": 1 }""").RootElement.Clone()
            }
        }, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ExecutionStepFailed);
        var entry = _logger.Entries.Where(e => e.EventId == 10269).ShouldHaveSingleItem();
        entry.Exception.ShouldBeSameAs(thrown);
    }

    private sealed class CapturingLogger : ILogger<InstanceCommandAppService>
    {
        public List<(int EventId, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((eventId.Id, exception));
    }
}
