using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Contracts;
using BBT.Workflow.Functions.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Evaluators;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>
/// The declarative function <c>output</c> block (Phase C2) through <see cref="FunctionAppService"/>: the response
/// document <c>{ data, statusCode, headers }</c>, <c>result</c> keyed by each task's response slot, the shared failure
/// path with the output script (<c>ExtensionExecutionFailed</c>), and the response cache storing the built response.
/// </summary>
public sealed class FunctionOutputDeclarativeTests : IDisposable
{
    private const string FunctionKey = "my-fn";

    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly ITaskCoordinator _taskCoordinator = Substitute.For<ITaskCoordinator>();
    private readonly IStateStoreCacheGateway _cacheGateway = Substitute.For<IStateStoreCacheGateway>();
    private readonly IScriptEngine _scriptEngine = Substitute.For<IScriptEngine>();
    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;
    private readonly FunctionAppService _service;

    /// <summary>Every log entry the service writes.</summary>
    private readonly CapturingLoggerProvider _logs = new();

    /// <summary>The task responses the coordinator files, keyed by response slot.</summary>
    private readonly Dictionary<string, object?> _taskResponses = new();

    public FunctionOutputDeclarativeTests()
    {
        var unitOfWorkManager = Substitute.For<IUnitOfWorkManager>();
        unitOfWorkManager
            .BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<IUnitOfWork>()));
        var services = new ServiceCollection();
        services.AddSingleton(unitOfWorkManager);
        services.AddLogging(logging => logging.AddProvider(_logs));
        services.AddTransient<ILazyServiceProvider, LazyServiceProvider>();
        _ambientServiceProvider = services.BuildServiceProvider();
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithWorkflow(Arg.Any<Definitions.Workflow?>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance?>()).Returns(builder);
        builder.WithBody(Arg.Any<object?>()).Returns(builder);
        builder.WithHeaders(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.WithQueryParameters(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.WithMetadata(Arg.Any<Dictionary<string, object>?>()).Returns(builder);
        builder.WithCaller(Arg.Any<ICallerAccessor?>()).Returns(builder);
        // A fresh context per call, as the real builder gives.
        builder.BuildAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ScriptContext(NullLogger<ScriptContext>.Instance)));
        var scriptContextFactory = Substitute.For<IScriptContextFactory>();
        scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);

        _taskCoordinator
            .ExecuteAsync(
                Arg.Any<IEnumerable<OnExecuteTask>>(), Arg.Any<Guid?>(), Arg.Any<TaskTrigger>(),
                Arg.Any<TaskExecutionOrigin>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var context = call.ArgAt<ScriptContext>(4);
                foreach (var (slot, response) in _taskResponses)
                    context.TaskResponse[slot] = response;
                return Task.FromResult(Result.Ok());
            });

        var validation = Substitute.For<IFunctionRequestValidationService>();
        validation
            .ValidateRequestAsync(
                Arg.Any<Function>(), Arg.Any<JsonElement?>(), Arg.Any<LazyScriptContext>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        _service = new FunctionAppService(
            serviceProvider: _ambientServiceProvider,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            instanceRepository: Substitute.For<IInstanceRepository>(),
            scriptContextFactory: scriptContextFactory,
            componentCacheStore: _componentCacheStore,
            currentSchema: Substitute.For<ICurrentSchema>(),
            taskCoordinator: _taskCoordinator,
            scriptEngine: _scriptEngine,
            keyEvaluator: Substitute.For<IDynamicExpressoValueEvaluator>(),
            cacheGateway: _cacheGateway,
            remoteInvoker: Substitute.For<IRemoteInvokerService>(),
            functionAccessPolicy: new FunctionAccessPolicy(),
            functionRequestValidationService: validation,
            functionExecutionJournal: Substitute.For<IFunctionExecutionJournal>(),
            currentUser: Substitute.For<ICurrentUser>(),
            callerAccessorFactory: new CallerAccessorFactory(
                Substitute.For<ICallerRoleResolver>(), NullLogger<CallerAccessorFactory>.Instance));
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task Block_Wrapped_UnderFunctionKey()
    {
        _taskResponses["myTask"] = new StandardTaskResponse { Data = Element("""{"limit":500}"""), StatusCode = 200 };
        Register(FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(
            """ "output": {"select":"$.myTask.body","into":"$.data","set":{"$.data.input":"input.amount","$.statusCode":"201"}} """),
            FunctionKey));

        var result = await Call("""{"amount":7}""");

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        // rawResponse false: { <function key>: data }; statusCode / headers are not part of a wrapped response.
        Json(result.Value!.Data).ShouldBe("""{"myFn":{"limit":500,"input":7}}""");
        result.Value.StatusCode.ShouldBeNull();
        result.Value.Headers.ShouldBeNull();
    }

    [Fact]
    public async Task Block_Raw_UsesStatusAndHeaders()
    {
        _taskResponses["myTask"] = new StandardTaskResponse
        {
            Data = Element("""{"id":"o-1"}"""),
            StatusCode = 200,
            Headers = new Dictionary<string, string> { ["X-Upstream"] = "u" }
        };
        Register(FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(
            """ "rawResponse": true, "output": {"set":{"$.data.orderId":"result.myTask.body.id","$.data.upstreamStatus":"result.myTask.statusCode","$.statusCode":"201","$.headers.location":"\"/orders/\" + result.myTask.body.id","$.headers.upstream":"result.myTask.headers[\"X-Upstream\"]"}} """),
            FunctionKey));

        var result = await Call();

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        Json(result.Value!.Data).ShouldBe("""{"orderId":"o-1","upstreamStatus":200}""");
        result.Value.StatusCode.ShouldBe(201);
        result.Value.Headers.ShouldNotBeNull();
        result.Value.Headers!["location"].ShouldBe("/orders/o-1");
        result.Value.Headers["upstream"].ShouldBe("u");
    }

    [Fact]
    public async Task Block_MultiTask_ReadsEachVariableKey()
    {
        _taskResponses["check"] = new StandardTaskResponse { Data = Element("""{"ok":true}"""), StatusCode = 200 };
        // Any other stored value is the body, with no headers / statusCode.
        _taskResponses["limits"] = Element("""{"daily":100}""");
        var attributes = $$"""
            {
              "scope": "D",
              "rawResponse": true,
              "task": { "order": 1, "task": {{FunctionTestFactory.Ref("t1", "sys-tasks")}}, "mapping": {{FunctionTestFactory.Rule("")}} },
              "onExecutionTasks": [
                { "order": 1, "variableKey": "check", "task": {{FunctionTestFactory.Ref("t1", "sys-tasks")}}, "mapping": {{FunctionTestFactory.Rule("")}} },
                { "order": 2, "variableKey": "limits", "task": {{FunctionTestFactory.Ref("t2", "sys-tasks")}}, "mapping": {{FunctionTestFactory.Rule("")}} },
                { "order": 3, "variableKey": "skipped", "task": {{FunctionTestFactory.Ref("t3", "sys-tasks")}}, "mapping": {{FunctionTestFactory.Rule("")}} }
              ],
              "output": {
                "select": "$.limits.body", "into": "$.data.limits",
                "set": {
                  "$.data.ok": "result.check.body.ok",
                  "$.data.checkStatus": "result.check.statusCode",
                  "$.data.limitsHasNoStatus": "result.limits.statusCode == null",
                  "$.data.limitsHasNoHeaders": "result.limits.headers == null"
                }
              }
            }
            """;
        Register(FunctionTestFactory.FromJson(attributes, FunctionKey));

        var result = await Call();

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        Json(result.Value!.Data).ShouldBe(
            """{"limits":{"daily":100},"ok":true,"checkStatus":200,"limitsHasNoStatus":true,"limitsHasNoHeaders":true}""");
    }

    [Fact]
    public async Task Block_MissingSlot_SelectWritesNothing()
    {
        _taskResponses["myTask"] = new StandardTaskResponse { Data = Element("""{"v":1}"""), StatusCode = 200 };
        Register(FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(
            """ "rawResponse": true, "output": {"select":"$.skipped.body","into":"$.data.fromSkipped","set":{"$.data.v":"result.myTask.body.v"}} """),
            FunctionKey));

        var result = await Call();

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        Json(result.Value!.Data).ShouldBe("""{"v":1}""");
    }

    [Fact]
    public async Task StatusCodeNotAnInteger_FailsLikeScript()
    {
        _taskResponses["myTask"] = new StandardTaskResponse { Data = Element("{}"), StatusCode = 200 };
        Register(FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(
            """ "rawResponse": true, "output": {"set":{"$.statusCode":"\"201\""}} """),
            FunctionKey));

        var result = await Call();

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ExtensionExecutionFailed);
        result.Error.Message.ShouldNotBeNull();
        result.Error.Message!.ShouldStartWith($"Function '{FunctionKey}' output");
        result.Error.Message.ShouldContain("$.statusCode is a JSON String");
    }

    [Fact]
    public async Task BlockFailure_LogsFunctionOutputMappingFailedOnce()
    {
        _taskResponses["myTask"] = new StandardTaskResponse { Data = Element("{}"), StatusCode = 200 };
        Register(FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(
            """ "rawResponse": true, "output": {"set":{"$.statusCode":"\"201\""}} """),
            FunctionKey));

        var result = await Call();

        result.IsSuccess.ShouldBeFalse();
        _logs.Entries.Count(e => e.EventId.Id == 10280).ShouldBe(1);
        _logs.Entries.Count(e => e.Level >= LogLevel.Error).ShouldBe(1);
    }

    [Fact]
    public async Task Wrapped_StatusCodeAndHeaders_AreIgnored_NotTypeChecked()
    {
        _taskResponses["myTask"] = new StandardTaskResponse { Data = Element("""{"v":1}"""), StatusCode = 200 };
        Register(FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(
            """ "output": {"set":{"$.data.v":"result.myTask.body.v","$.statusCode":"\"x\"","$.headers":"1"}} """),
            FunctionKey));

        var result = await Call();

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        Json(result.Value!.Data).ShouldBe("""{"myFn":{"v":1}}""");
        result.Value.StatusCode.ShouldBeNull();
        result.Value.Headers.ShouldBeNull();
    }

    [Fact]
    public async Task Block_HasNoCaller_UserIsUnavailable()
    {
        // Publish rejects a function output reading `user`; a stored definition that does so still has no caller.
        _taskResponses["myTask"] = new StandardTaskResponse { Data = Element("{}"), StatusCode = 200 };
        Register(FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(
            """ "output": {"set":{"$.data.caller":"user.available"}} """),
            FunctionKey));

        var result = await Call(headers: new Dictionary<string, string?> { ["role"] = "admin" });

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        Json(result.Value!.Data).ShouldBe("""{"myFn":{"caller":false}}""");
    }

    [Fact]
    public async Task FailingSetExpression_FailsLikeScript()
    {
        _taskResponses["myTask"] = new StandardTaskResponse { Data = Element("""{"amount":"abc"}"""), StatusCode = 200 };
        Register(FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(
            """ "output": {"set":{"$.data.n":"result.myTask.body.amount.AsInt32()"}} """),
            FunctionKey));

        var result = await Call();

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ExtensionExecutionFailed);
        result.Error.Message!.ShouldStartWith($"Function '{FunctionKey}' output");
    }

    [Fact]
    public async Task CachedFunction_BlockOutput_HitReturnsSameBody()
    {
        _taskResponses["myTask"] = new StandardTaskResponse { Data = Element("""{"rate":1.25}"""), StatusCode = 200 };
        Register(FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(
            """ "cache": {"key":"fn:block","ttlInSeconds":60}, "output": {"select":"$.myTask.body","into":"$.data","set":{"$.data.cached":"true"}} """),
            FunctionKey));

        // The gateway as a store: a miss until something is written, then the written value as JSON.
        JsonElement? stored = null;
        _cacheGateway
            .SetAsync("fn:block", Arg.Any<object?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                stored = JsonSerializer.SerializeToElement(call.ArgAt<object?>(1), JsonSerializerConstants.JsonOptions);
                return Task.FromResult(true);
            });
        _cacheGateway
            .GetAsync("fn:block", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(stored is { } value
                ? new CacheGetResult(CacheOk: true, Hit: true, Value: value)
                : new CacheGetResult(CacheOk: true, Hit: false, Value: default)));

        var miss = await Call();
        var hit = await Call();

        miss.IsSuccess.ShouldBeTrue(miss.IsSuccess ? null : miss.Error.Message);
        hit.IsSuccess.ShouldBeTrue(hit.IsSuccess ? null : hit.Error.Message);
        Json(miss.Value!.Data).ShouldBe("""{"myFn":{"rate":1.25,"cached":true}}""");
        Json(hit.Value!.Data).ShouldBe(Json(miss.Value.Data));
        // The second call is served from the cache: the tasks (and the block) ran once.
        await _taskCoordinator.Received(1).ExecuteAsync(
            Arg.Any<IEnumerable<OnExecuteTask>>(), Arg.Any<Guid?>(), Arg.Any<TaskTrigger>(),
            Arg.Any<TaskExecutionOrigin>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScriptStillRuns_WhenNoBlock()
    {
        _taskResponses["myTask"] = new StandardTaskResponse { Data = Element("{}"), StatusCode = 200 };
        Register(FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(
            """ "rawResponse": true, "output": {"location":"./output.csx","code":"","encoding":"NAT"} """),
            FunctionKey));
        var handler = Substitute.For<IOutputHandler>();
        handler.OutputHandler(Arg.Any<ScriptContext>())
            .Returns(Task.FromResult(new ScriptResponse { Data = new Dictionary<string, object> { ["from"] = "script" }, StatusCode = 202 }));
        _scriptEngine.CompileToInstanceAsync<IOutputHandler>(Arg.Any<ScriptCode>())
            .ReturnsForAnyArgs(Task.FromResult(handler));

        var result = await Call();

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        Json(result.Value!.Data).ShouldBe("""{"from":"script"}""");
        result.Value.StatusCode.ShouldBe(202);
        await handler.Received(1).OutputHandler(Arg.Any<ScriptContext>());
    }

    [Fact]
    public async Task FailingScript_StillFailsWithExtensionExecutionFailed()
    {
        _taskResponses["myTask"] = new StandardTaskResponse { Data = Element("{}"), StatusCode = 200 };
        Register(FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(
            """ "output": {"location":"./output.csx","code":"","encoding":"NAT"} """),
            FunctionKey));
        var handler = Substitute.For<IOutputHandler>();
        handler.OutputHandler(Arg.Any<ScriptContext>())
            .Returns<Task<ScriptResponse>>(_ => throw new InvalidOperationException("boom"));
        _scriptEngine.CompileToInstanceAsync<IOutputHandler>(Arg.Any<ScriptCode>())
            .ReturnsForAnyArgs(Task.FromResult(handler));

        var result = await Call();

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ExtensionExecutionFailed);
        result.Error.Message!.ShouldStartWith($"Function '{FunctionKey}' output ScriptMapping failed:");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private void Register(Function function) =>
        _componentCacheStore
            .GetFunctionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Function>.Ok(function));

    private Task<Result<FunctionResponseOutput>> Call(
        string? body = null, Dictionary<string, string?>? headers = null) =>
        _service.GetFunctionByKeyAsync(
            FunctionKey, FunctionTestFactory.Domain,
            headers: headers,
            body: body is null ? null : Element(body),
            httpMethod: body is null ? "GET" : "POST");

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Json(object? value) => JsonSerializer.Serialize(value, JsonSerializerConstants.JsonOptions);
}

/// <summary>Records every log entry (level and event id) written through the loggers it creates.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<(LogLevel Level, EventId EventId)> _entries = new();

    /// <summary>The entries written so far.</summary>
    public IReadOnlyList<(LogLevel Level, EventId EventId)> Entries
    {
        get { lock (_entries) return _entries.ToArray(); }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner._entries)
                owner._entries.Add((logLevel, eventId));
        }
    }
}
