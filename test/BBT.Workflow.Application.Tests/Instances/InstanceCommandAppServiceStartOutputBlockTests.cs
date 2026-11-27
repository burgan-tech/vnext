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
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Phase C1: a start transition's declarative <c>output</c> block maps the start payload into the initial data version,
/// where a start mapping script's result goes (<c>MapAndAppendInstanceDataAsync</c>).
/// </summary>
public sealed class InstanceCommandAppServiceStartOutputBlockTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string Flow = "test-flow";
    private const string Version = "1.0.0";

    private readonly IInstanceDataWriteService _dataWriteService = Substitute.For<IInstanceDataWriteService>();
    private readonly ICallerAccessorFactory _callerFactory = Substitute.For<ICallerAccessorFactory>();
    private readonly CapturingLogger _logger = new();
    private readonly InstanceCommandAppService _service;
    private readonly IServiceProvider _ambient;
    private readonly IServiceProvider? _previousAmbient;

    public InstanceCommandAppServiceStartOutputBlockTests()
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
        var start = Transition.Create("start", null, "state1", TriggerType.Manual, "Patch");
        BBT.Workflow.Application.Tests.Execution.Transitions.TransitionDataMapperDeclarativeTests.SetOutput(
            start,
            BBT.Workflow.Application.Tests.Execution.Transitions.TransitionDataMapperDeclarativeTests.Block(
                set: [("$.application.amount", "input.amount"), ("$.byUser", "user.available")]));
        workflow.SetStartTransition(start);
        _callerFactory.ForHop(default, default, default).ReturnsForAnyArgs(BBT.Workflow.Scripting.Caller.NullCallerAccessor.Instance);
        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithWorkflow(Arg.Any<Definitions.Workflow>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance>()).Returns(builder);
        builder.WithTransition(Arg.Any<Transition>()).Returns(builder);
        builder.WithBody(Arg.Any<object>()).Returns(builder);
        builder.WithHeaders(Arg.Any<Dictionary<string, string?>>()).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>()).Returns(_ => new ScriptContext(Substitute.For<ILogger<ScriptContext>>()));
        var scriptContextFactory = Substitute.For<IScriptContextFactory>();
        scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);
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
        var mapper = new TransitionDataMapper(
            Substitute.For<IScriptEngine>(), scriptContextFactory, instanceRepository, Substitute.For<IRuntimeInfoProvider>(),
            callerAccessorFactory: _callerFactory);

        _service = new InstanceCommandAppService(
            serviceProvider: _ambient,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            workflowExecutionService: Substitute.For<IWorkflowExecutionService>(),
            componentCacheStore: componentCacheStore,
            instanceRepository: instanceRepository,
            instanceDataWriteService: _dataWriteService,
            instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
            backgroundJobService: Substitute.For<IBackgroundJobService>(),
            guidGenerator: Substitute.For<IGuidGenerator>(),
            headerService: Substitute.For<IHeaderService>(),
            transitionDataMapper: mapper,
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
    public async Task StartTransition_OutputBlock_AppendsInitialData()
    {
        var result = await _service.StartAsync(new StartInstanceInput(Domain, Flow, Version)
        {
            Instance = new CreateInstanceInput
            {
                Key = "k-" + Guid.NewGuid(),
                Attributes = JsonDocument.Parse("""{ "amount": 3, "noise": true }""").RootElement.Clone()
            }
        }, CancellationToken.None);

        // The start's own pipeline is a substitute; what matters is the initial data version.
        await _dataWriteService.Received(1).AppendAsync(
            Arg.Any<Instance>(),
            Arg.Is<JsonData>(d => d.Json.Replace(" ", "") == """{"application":{"amount":3},"byUser":false}"""),
            Arg.Any<VersionStrategy?>(), Arg.Any<CancellationToken>(), Arg.Any<Definitions.Workflow?>());
        _callerFactory.Received().ForHop(ExecutionActor.User, Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>());
        _ = result;
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
