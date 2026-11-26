using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Application.Tests.Telemetry;
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
using BBT.Workflow.Logging;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// The workflow's declarative <c>output</c> block (Phase C2): it fills <c>{ data, statusCode, headers }</c> where an
/// output script's <see cref="ScriptResponse"/> goes — <c>input</c> is the hop's request payload, <c>user</c> the hop's
/// caller, an absent <c>data</c> is an empty response, and any failure (an expression, a wrongly typed
/// <c>statusCode</c> / <c>headers</c>) takes the script's failure path: logged, span error, standard envelope.
/// Sub-flow instances never run it.
/// </summary>
public sealed class WorkflowOutputDeclarativeTests
{
    private const string Domain = "test-domain";
    private const string Flow = "output-flow";
    private const string Version = "1.0.0";
    private const string ScriptJson = """{"location":"./output.csx","code":"YQ==","type":"L","encoding":"B64"}""";

    private readonly IScriptEngine _scriptEngine = Substitute.For<IScriptEngine>();
    private readonly ICallerAccessorFactory _callerFactory = Substitute.For<ICallerAccessorFactory>();
    private readonly CapturingLogger _logger = new();

    [Fact]
    public async Task Block_ReplacesEnvelope()
    {
        var workflow = WorkflowWithOutput("""{"set":{"$.data.id":"instance.key","$.statusCode":"201","$.headers.location":"\"/orders/\" + instance.key"}}""");

        var result = await Service().ApplyAsync(workflow, Context(), Hop(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var output = result.Value.ShouldNotBeNull();
        Json(output.Data).ShouldBe("""{"id":"ctx-key"}""");
        output.StatusCode.ShouldBe(201);
        output.Headers.ShouldNotBeNull();
        output.Headers!["location"].ShouldBe("/orders/ctx-key");
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Block_EmptyData_IsEmptyResponse()
    {
        var workflow = WorkflowWithOutput("""{"set":{"$.statusCode":"204"}}""");

        var result = await Service().ApplyAsync(workflow, Context(), Hop(), CancellationToken.None);

        // The block ran: an output (not the envelope) with no body.
        result.IsSuccess.ShouldBeTrue();
        var output = result.Value.ShouldNotBeNull();
        output.Data.ShouldBeNull();
        output.StatusCode.ShouldBe(204);
        output.Headers.ShouldBeNull();
    }

    [Fact]
    public async Task Block_ReadsInputAndUser()
    {
        var caller = Substitute.For<ICallerAccessor>();
        caller.IsAvailable.Returns(true);
        var headers = new Dictionary<string, string?> { ["x-req"] = "r-1" };
        _callerFactory.ForHop(ExecutionActor.User, headers, Arg.Any<CancellationToken>()).Returns(caller);
        var workflow = WorkflowWithOutput(
            """{"set":{"$.data.amount":"input.amount","$.data.caller":"user.available","$.data.request":"headers[\"x-req\"]","$.data.stored":"data.customer.name"}}""");
        var hop = new WorkflowOutputHop(JsonDocument.Parse("""{"amount":5}""").RootElement.Clone(), ExecutionActor.User, headers);

        var result = await Service().ApplyAsync(workflow, Context(headers: headers), hop, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        // `input` is the hop's payload (not the stored data), `user` the hop's caller, `data` the stored data.
        Json(result.Value.ShouldNotBeNull().Data)
            .ShouldBe("""{"amount":5,"caller":true,"request":"r-1","stored":"Ada"}""");
        _callerFactory.Received(1).ForHop(ExecutionActor.User, headers, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Block_NoCaller_UserIsUnavailable()
    {
        var workflow = WorkflowWithOutput("""{"set":{"$.data.caller":"user.available"}}""");

        var result = await Service(callerFactory: null).ApplyAsync(workflow, Context(), Hop(), CancellationToken.None);

        Json(result.Value.ShouldNotBeNull().Data).ShouldBe("""{"caller":false}""");
    }

    [Fact]
    public async Task Block_Select_CopiesIntoData()
    {
        // Parked from Phase C1: a WorkflowOutput `select` applied at runtime.
        var workflow = WorkflowWithOutput("""{"select":"$.data.application","into":"$.data","set":{"$.data.seen":"true"}}""");

        var result = await Service().ApplyAsync(
            workflow, Context("""{"application":{"id":12,"status":"open"},"other":1}"""), Hop(), CancellationToken.None);

        Json(result.Value.ShouldNotBeNull().Data).ShouldBe("""{"id":12,"status":"open","seen":true}""");
    }

    [Fact]
    public async Task BlockFailure_FallsBackToEnvelope()
    {
        // data.amount is a string: AsInt32() throws at runtime.
        var workflow = WorkflowWithOutput("""{"set":{"$.data.amount":"data.amount.AsInt32()"}}""");
        using var spans = new TraceSpanCollector("BBT.Workflow.Scripting");

        var result = await Service().ApplyAsync(
            workflow, Context("""{"amount":"not-a-number"}"""), Hop(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
        _logger.Entries.ShouldContain(e => e.EventId == OutputFailedId && e.Level == LogLevel.Error);
        var span = spans.Spans.Where(a => a.DisplayName == "Script.Execute").ShouldHaveSingleItem();
        span.GetTagItem(TelemetryConstants.TagNames.ScriptKind).ShouldBe("workflowOutput");
        span.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Theory]
    [InlineData("""{"set":{"$.statusCode":"\"201\""}}""")]
    [InlineData("""{"set":{"$.statusCode":"201.5"}}""")]
    [InlineData("""{"set":{"$.headers.count":"5"}}""")]
    [InlineData("""{"set":{"$.headers":"\"x\""}}""")]
    public async Task WronglyTypedMember_FallsBackToEnvelope(string block)
    {
        var workflow = WorkflowWithOutput(block);
        using var spans = new TraceSpanCollector("BBT.Workflow.Scripting");

        var result = await Service().ApplyAsync(workflow, Context(), Hop(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
        _logger.Entries.ShouldContain(e => e.EventId == OutputFailedId && e.Level == LogLevel.Error);
        spans.Spans.Where(a => a.DisplayName == "Script.Execute").ShouldHaveSingleItem()
            .Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task ScriptStillRuns_WhenNoBlock()
    {
        var workflow = Read($$"""{"type":"F","states":[],"output":{{ScriptJson}}}""");
        var handler = Substitute.For<IOutputHandler>();
        handler.OutputHandler(Arg.Any<ScriptContext>())
            .Returns(Task.FromResult(new ScriptResponse { Data = new Dictionary<string, object> { ["from"] = "script" }, StatusCode = 202 }));
        _scriptEngine.CompileToInstanceAsync<IOutputHandler>(Arg.Any<ScriptCode>())
            .ReturnsForAnyArgs(Task.FromResult(handler));

        var result = await Service().ApplyAsync(workflow, Context(), Hop(), CancellationToken.None);

        var output = result.Value.ShouldNotBeNull();
        output.StatusCode.ShouldBe(202);
        Json(output.Data).ShouldBe("""{"from":"script"}""");
        await handler.Received(1).OutputHandler(Arg.Any<ScriptContext>());
        _callerFactory.DidNotReceiveWithAnyArgs().ForHop(default, default, default);
    }

    [Fact]
    public async Task NoOutput_KeepsEnvelope()
    {
        var result = await Service().ApplyAsync(Read("""{"type":"F"}"""), Context(), Hop(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task SubFlowInstance_BlockIgnored()
    {
        using var host = new CommandHost(WorkflowWithOutput("""{"set":{"$.statusCode":"201"}}"""));
        var instance = Instance.Create(host.InstanceId, Flow, Version, "key");
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = WorkflowType.SubFlow.Code;
        host.Reload(instance);

        var result = await host.TransitionAsync(Input());

        result.IsSuccess.ShouldBeTrue();
        result.Value!.HasOutputResponse.ShouldBeFalse();
        host.ScriptContextFactory.DidNotReceiveWithAnyArgs().NewBuilder(default!);
        await host.OutputService.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task RootInstance_Block_RunsWithTheHop()
    {
        using var host = new CommandHost(WorkflowWithOutput("""{"set":{"$.statusCode":"201"}}"""));
        host.Reload(Instance.Create(host.InstanceId, Flow, Version, "key"));
        WorkflowOutputHop? seen = null;
        host.OutputService.ApplyAsync(
                Arg.Any<Definitions.Workflow>(), Arg.Any<ScriptContext>(), Arg.Do<WorkflowOutputHop?>(h => seen = h),
                Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowOutputResult?>.Ok(new WorkflowOutputResult(null, 201, null)));
        var input = Input();

        var result = await host.TransitionAsync(input);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.HasOutputResponse.ShouldBeTrue();
        result.Value.OutputStatusCode.ShouldBe(201);
        seen.ShouldNotBeNull();
        ((JsonElement)seen!.Input!).GetRawText().ShouldBe("""{"amount":5}""");
        seen.Actor.ShouldBe(ExecutionActor.User);
        seen.Headers.ShouldBeSameAs(input.Headers);
        // The block reads the request headers as `headers`.
        host.Builder.Received(1).WithHeaders(Arg.Is<Dictionary<string, string?>>(h => h["x-req"] == "r-1"));
    }

    [Fact]
    public async Task Transition_FileMember_BlockEchoesTheHandleNotTheBytes()
    {
        // A real output service echoing `input`; the pipeline's file admission is simulated at its seam: it writes
        // the handle payload back to the WorkflowExecutionContext (and context.HopInput), never to TransitionInput.
        const string handle = """{"doc":{"component":"files","file":"f-1","name":"a.txt","mimeType":"text/plain","size":3,"eTag":"e"}}""";
        using var host = new CommandHost(
            WorkflowWithOutput("""{"select":"$.input","into":"$.data"}"""),
            new WorkflowOutputMappingService(Substitute.For<IScriptEngine>(), NullLogger<WorkflowOutputMappingService>.Instance));
        host.Reload(Instance.Create(host.InstanceId, Flow, Version, "key"));
        host.OnExecute = ctx => ctx.Data!.Attributes = JsonDocument.Parse(handle).RootElement.Clone();
        var input = Input("""{"doc":{"content":"QUJD","name":"a.txt"}}""");

        var result = await host.TransitionAsync(input);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.HasOutputResponse.ShouldBeTrue();
        Json(result.Value.OutputData).ShouldBe(handle);
        Json(result.Value.OutputData).ShouldNotContain("QUJD");
        // The request DTO still holds the bytes — which is why it is not the source of `input`.
        input.Data!.Attributes!.Value.GetRawText().ShouldContain("QUJD");
    }

    [Fact]
    public async Task Transition_RelayedToActiveSubflow_InputIsNull()
    {
        using var host = new CommandHost(WorkflowWithOutput("""{"set":{"$.statusCode":"201"}}"""));
        host.Reload(Instance.Create(host.InstanceId, Flow, Version, "key"));
        host.OnExecute = ctx => ctx.HopPayloadNotAdmitted = true;
        var seen = host.CaptureHop();

        var result = await host.TransitionAsync(Input());

        result.IsSuccess.ShouldBeTrue();
        seen().ShouldNotBeNull();
        seen()!.Input.ShouldBeNull();
        seen()!.Actor.ShouldBe(ExecutionActor.User);
    }

    [Fact]
    public async Task Transition_ProxiedToActiveSubflow_InputIsNull()
    {
        using var host = new CommandHost(WorkflowWithOutput("""{"set":{"$.statusCode":"201"}}"""));
        host.Reload(Instance.Create(host.InstanceId, Flow, Version, "key"));
        host.Proxy.TryProxyAsync(
                Arg.Any<InstanceExecutionSnapshot>(), Arg.Any<Definitions.Workflow>(), Arg.Any<string>(),
                Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Result<TransitionOutput>?>(Result<TransitionOutput>.Ok(
                new TransitionOutput { Id = host.InstanceId, Status = InstanceStatus.Active, ExecutedAsync = false })));
        var seen = host.CaptureHop();

        var result = await host.TransitionAsync(Input());

        result.IsSuccess.ShouldBeTrue();
        seen().ShouldNotBeNull();
        seen()!.Input.ShouldBeNull();
    }

    [Fact]
    public async Task IdempotentStart_InputIsNull()
    {
        using var host = new CommandHost(WorkflowWithOutput("""{"set":{"$.statusCode":"201"}}"""));
        var existing = Instance.Create(host.InstanceId, Flow, Version, "key");
        host.Repository.FindActiveByKeyLeanAsync("key", Arg.Any<CancellationToken>()).Returns(existing);
        host.Reload(existing);
        var seen = host.CaptureHop();

        var result = await host.StartAsync(new StartInstanceInput(Domain, Flow, sync: true)
        {
            Instance = new CreateInstanceInput
            {
                Key = "key",
                Attributes = JsonDocument.Parse("""{"amount":5}""").RootElement.Clone()
            },
            Headers = new Dictionary<string, string?> { ["x-req"] = "r-1" }
        });

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Id.ShouldBe(host.InstanceId);
        seen().ShouldNotBeNull();
        seen()!.Input.ShouldBeNull();
        seen()!.Actor.ShouldBe(ExecutionActor.User);
    }

    private static int OutputFailedId =>
        typeof(WorkflowLogs).GetMethod(nameof(WorkflowLogs.WorkflowOutputScriptFailed))!
            .GetCustomAttributes(typeof(LoggerMessageAttribute), false).Cast<LoggerMessageAttribute>().Single().EventId;

    private WorkflowOutputMappingService Service() => Service(_callerFactory);

    private WorkflowOutputMappingService Service(ICallerAccessorFactory? callerFactory) =>
        new(_scriptEngine, _logger, callerFactory);

    private static WorkflowOutputHop Hop() =>
        new(JsonDocument.Parse("{}").RootElement.Clone(), ExecutionActor.User, new Dictionary<string, string?>());

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static ScriptContext Context(
        string data = """{"customer":{"name":"Ada"}}""", Dictionary<string, string?>? headers = null) =>
        new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(InstanceWithData(data))
            .SetHeaders(headers ?? new Dictionary<string, string?>())
            .Build();

    private static Instance InstanceWithData(string json)
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, Version, "ctx-key");
        var row = new InstanceData(Guid.NewGuid(), instance.Id, Version, new JsonData(json), true);
        typeof(InstanceData).GetProperty(nameof(InstanceData.VersionNo))!.SetValue(row, 1L);
        instance.AcceptPersistedData(row);
        return instance;
    }

    private static Definitions.Workflow Read(string json)
    {
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, JsonSerializerConstants.JsonOptions)!;
        workflow.SetReference(new Reference(Flow, Domain, "sys-flows", Version));
        return workflow;
    }

    private static Definitions.Workflow WorkflowWithOutput(string block)
    {
        var workflow = Read($$"""{"type":"F","states":[],"output":{{block}}}""");
        workflow.OutputBlock.ShouldNotBeNull();
        return workflow;
    }

    private static TransitionInput Input(string body = """{"amount":5}""") =>
        new(Domain, Flow, new TransitionDataInput(JsonDocument.Parse(body).RootElement.Clone()), sync: true)
        {
            Headers = new Dictionary<string, string?> { ["x-req"] = "r-1" },
            RouteValues = new Dictionary<string, string?>()
        };

    /// <summary>An <see cref="InstanceCommandAppService"/> on substitutes, for the sync enrichment gate.</summary>
    private sealed class CommandHost : IDisposable
    {
        private readonly IWorkflowExecutionService _executionService = Substitute.For<IWorkflowExecutionService>();
        private readonly IServiceProvider _ambient;
        private readonly IServiceProvider? _previousAmbient;
        private readonly InstanceCommandAppService _service;

        public Guid InstanceId { get; } = Guid.NewGuid();
        public IScriptContextFactory ScriptContextFactory { get; } = Substitute.For<IScriptContextFactory>();
        public IScriptContextBuilder Builder { get; } = Substitute.For<IScriptContextBuilder>();
        public IWorkflowOutputMappingService OutputService { get; }
        public IInstanceRepository Repository { get; } = Substitute.For<IInstanceRepository>();
        public BBT.Workflow.SubFlow.ISubflowProxyService Proxy { get; } = Substitute.For<BBT.Workflow.SubFlow.ISubflowProxyService>();

        /// <summary>Runs on the dispatched <see cref="WorkflowExecutionContext"/>, as the pipeline would mutate it.</summary>
        public Action<WorkflowExecutionContext>? OnExecute { get; set; }

        public CommandHost(Definitions.Workflow workflow, IWorkflowOutputMappingService? outputService = null)
        {
            OutputService = outputService ?? Substitute.For<IWorkflowOutputMappingService>();
            var uow = Substitute.For<IUnitOfWorkManager>();
            uow.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
            var services = new ServiceCollection();
            services.AddSingleton(uow);
            _ambient = services.BuildServiceProvider();
            _previousAmbient = AmbientServiceProvider.Current;
            AmbientServiceProvider.Current = _ambient;

            var cache = Substitute.For<IComponentCacheStore>();
            cache.GetFlowAsync(Domain, Flow, Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Result<Definitions.Workflow>.Ok(workflow));

            Repository.GetExecutionSnapshotAsync(InstanceId.ToString(), Arg.Any<CancellationToken>())
                .Returns(new InstanceExecutionSnapshot(InstanceId, "key", InstanceStatus.Active, "state1", Flow, Version, false));
            _executionService.ExecuteTransitionAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    OnExecute?.Invoke(call.Arg<WorkflowExecutionContext>());
                    return Result<TransitionOutput>.Ok(new TransitionOutput { Id = InstanceId, Status = InstanceStatus.Active });
                });

            Builder.WithWorkflow(Arg.Any<Definitions.Workflow>()).Returns(Builder);
            Builder.WithInstance(Arg.Any<Instance>()).Returns(Builder);
            Builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(Builder);
            Builder.WithTransition(Arg.Any<string>()).Returns(Builder);
            Builder.WithBody(Arg.Any<object>()).Returns(Builder);
            Builder.WithHeaders(Arg.Any<Dictionary<string, string?>>()).Returns(Builder);
            Builder.BuildAsync(Arg.Any<CancellationToken>()).Returns(_ => new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
                .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
                .Build());
            ScriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(Builder);

            _service = new InstanceCommandAppService(
                serviceProvider: _ambient,
                runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
                workflowExecutionService: _executionService,
                componentCacheStore: cache,
                instanceRepository: Repository,
                instanceDataWriteService: Substitute.For<IInstanceDataWriteService>(),
                instanceJobRepository: Substitute.For<IInstanceJobRepository>(),
                backgroundJobService: Substitute.For<IBackgroundJobService>(),
                guidGenerator: Substitute.For<IGuidGenerator>(),
                headerService: Substitute.For<IHeaderService>(),
                transitionDataMapper: Substitute.For<ITransitionDataMapper>(),
                transitionValidationService: Substitute.For<ITransitionValidationService>(),
                transitionAdmissionService: Substitute.For<ITransitionAdmissionService>(),
                representationEtagService: Substitute.For<IRepresentationEtagService>(),
                instanceDataReadService: new InstanceDataReadService(Substitute.For<ISchemaFieldFilterService>()),
                scriptContextFactory: ScriptContextFactory,
                timerEvaluator: Substitute.For<ITimerEvaluator>(),
                transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>(),
                cancellationService: Substitute.For<IInstanceCancellationService>(),
                longPollAckResumeService: Substitute.For<ILongPollAckResumeService>(),
                instanceCommandGateway: Substitute.For<IInstanceCommandGateway>(),
                workflowOutputMappingService: OutputService,
                fileOffloadService: BBT.Workflow.Application.Files.FileTestDoubles.PassThroughOffload(),
                rawBodyProvider: Substitute.For<IRequestRawBodyProvider>(),
                subflowProxyService: Proxy,
                logger: Substitute.For<ILogger<InstanceCommandAppService>>());
        }

        /// <summary>Captures the hop the (substituted) output service receives; the getter reads the last one.</summary>
        public Func<WorkflowOutputHop?> CaptureHop()
        {
            WorkflowOutputHop? seen = null;
            OutputService.ApplyAsync(
                    Arg.Any<Definitions.Workflow>(), Arg.Any<ScriptContext>(), Arg.Do<WorkflowOutputHop?>(h => seen = h),
                    Arg.Any<CancellationToken>())
                .Returns(Result<WorkflowOutputResult?>.Ok(new WorkflowOutputResult(null, 201, null)));
            return () => seen;
        }

        public void Reload(Instance instance) =>
            Repository.FindByIdentifierAsReadOnlyAsync(InstanceId.ToString(), Arg.Any<CancellationToken>())
                .Returns(instance);

        public Task<Result<StartInstanceOutput>> StartAsync(StartInstanceInput input) =>
            _service.StartAsync(input, CancellationToken.None);

        public Task<Result<TransitionOutput>> TransitionAsync(TransitionInput input) =>
            _service.TransitionAsync(InstanceId.ToString(), "regular-transition", input, CancellationToken.None);

        public void Dispose()
        {
            AmbientServiceProvider.Current = _previousAmbient;
            (_ambient as IDisposable)?.Dispose();
        }
    }

    private sealed class CapturingLogger : ILogger<WorkflowOutputMappingService>
    {
        public List<(int EventId, LogLevel Level)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((eventId.Id, logLevel));
    }
}
