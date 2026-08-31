using System;
using System.Collections.Generic;
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
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Evaluators;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>
/// Unit tests for function scope enforcement in <see cref="FunctionAppService"/>:
/// <list type="bullet">
/// <item>Domain — always runs (exempt from every scope restriction).</item>
/// <item>Instance — only runs when an instance exists.</item>
/// <item>Flow — requires an instance and the function to be declared in the instance's flow.</item>
/// </list>
/// Violations return <see cref="WorkflowErrorCodes.FunctionScopeNotSatisfied"/> (HTTP 403).
/// </summary>
public sealed class FunctionAppServiceScopeTests : IDisposable
{
    private const string TestDomain = "test-domain";
    private const string TestFlow = "test-flow";
    private const string TestVersion = "1.0.0";
    private const string FunctionKey = "send-otp";

    private readonly IInstanceRepository _instanceRepository;
    private readonly IComponentCacheStore _componentCacheStore;
    private readonly ITaskCoordinator _taskCoordinator;
    private readonly IStateStoreCacheGateway _cacheGateway;
    private readonly IDynamicExpressoValueEvaluator _keyEvaluator;
    private readonly IFunctionRequestValidationService _functionRequestValidationService;
    private readonly FunctionAppService _service;

    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public FunctionAppServiceScopeTests()
    {
        _instanceRepository = Substitute.For<IInstanceRepository>();
        _componentCacheStore = Substitute.For<IComponentCacheStore>();
        _taskCoordinator = Substitute.For<ITaskCoordinator>();
        _cacheGateway = Substitute.For<IStateStoreCacheGateway>();
        _keyEvaluator = Substitute.For<IDynamicExpressoValueEvaluator>();
        _functionRequestValidationService = Substitute.For<IFunctionRequestValidationService>();
        _functionRequestValidationService
            .ValidateRequestAsync(
                Arg.Any<Function>(),
                Arg.Any<JsonElement?>(),
                Arg.Any<LazyScriptContext>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        // Ambient provider needed by PostSharp UnitOfWork interception.
        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager
            .BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockUoW));
        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
        services.AddLogging();
        // ApplicationService.Logger resolves through ILazyServiceProvider.
        services.AddTransient<ILazyServiceProvider, LazyServiceProvider>();
        _ambientServiceProvider = services.BuildServiceProvider();
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientServiceProvider;

        // Successful task execution + a buildable script context for the allow paths.
        var scriptContext = new ScriptContext(NullLogger<ScriptContext>.Instance);
        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithWorkflow(Arg.Any<Definitions.Workflow?>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance?>()).Returns(builder);
        builder.WithBody(Arg.Any<object?>()).Returns(builder);
        builder.WithHeaders(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.WithQueryParameters(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.WithMetadata(Arg.Any<Dictionary<string, object>?>()).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>()).Returns(scriptContext);

        var scriptContextFactory = Substitute.For<IScriptContextFactory>();
        scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);

        _taskCoordinator
            .ExecuteAsync(
                Arg.Any<IEnumerable<OnExecuteTask>>(),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<TaskExecutionOrigin>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        _service = new FunctionAppService(
            serviceProvider: _ambientServiceProvider,
            runtimeInfoProvider: Substitute.For<IRuntimeInfoProvider>(),
            instanceRepository: _instanceRepository,
            scriptContextFactory: scriptContextFactory,
            componentCacheStore: _componentCacheStore,
            currentSchema: Substitute.For<ICurrentSchema>(),
            taskCoordinator: _taskCoordinator,
            scriptEngine: Substitute.For<IScriptEngine>(),
            keyEvaluator: _keyEvaluator,
            cacheGateway: _cacheGateway,
            remoteInvoker: Substitute.For<IRemoteInvokerService>(),
            // The real policy, so these tests keep covering the scope and role gates end to end
            // after they moved out of FunctionAppService.
            functionAccessPolicy: new FunctionAccessPolicy(
                Substitute.For<ICurrentUser>(),
                Substitute.For<ITransitionAuthorizationManager>()),
            functionRequestValidationService: _functionRequestValidationService);
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
        (_ambientServiceProvider as IDisposable)?.Dispose();
    }

    // ─── Domain-level endpoint (GetFunctionByKeyAsync — no instance) ────────────

    [Fact]
    public async Task ByKey_DomainScope_Succeeds()
    {
        SetupFunction(TaskScope.Domain);

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ByKey_InstanceScope_Returns403()
    {
        SetupFunction(TaskScope.Instance);

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FunctionScopeNotSatisfied);
    }

    [Fact]
    public async Task ByKey_FlowScope_Returns403()
    {
        SetupFunction(TaskScope.Flow);

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FunctionScopeNotSatisfied);
    }

    // ─── Verb enforcement ───────────────────────────────────────────────────────

    [Fact]
    public async Task ByKey_NoVerbsDeclared_AcceptsAnyVerb()
    {
        SetupFunction(TaskScope.Domain);

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain, httpMethod: "DELETE");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ByKey_NullHttpMethod_SkipsVerbCheck()
    {
        SetupFunction(TaskScope.Domain, verbs: ["POST"]);

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain, httpMethod: null);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ByKey_DeclaredVerb_Succeeds()
    {
        SetupFunction(TaskScope.Domain, verbs: ["POST", "PATCH"]);

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain, httpMethod: "PATCH");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ByKey_UndeclaredVerb_Returns405Error()
    {
        SetupFunction(TaskScope.Domain, verbs: ["POST", "PATCH"]);

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain, httpMethod: "DELETE");

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FunctionVerbNotAllowed);
        // Target carries the Allow header value.
        result.Error.Target.ShouldBe("POST, PATCH");
    }

    [Fact]
    public async Task ByKey_VerbComparisonIsCaseInsensitive()
    {
        SetupFunction(TaskScope.Domain, verbs: ["post"]);

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain, httpMethod: "POST");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ByKey_InputSchemaValidationFailure_ShortCircuitsBeforeTasks()
    {
        SetupFunction(TaskScope.Domain);
        _functionRequestValidationService
            .ValidateRequestAsync(
                Arg.Any<Function>(),
                Arg.Any<JsonElement?>(),
                Arg.Any<LazyScriptContext>(),
                Arg.Any<IReadOnlyDictionary<string, string?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Fail(Error.Validation("schema.invalid", "body does not match schema")));

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain, httpMethod: "POST");

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("schema.invalid");
        await _taskCoordinator.DidNotReceive().ExecuteAsync(
            Arg.Any<IEnumerable<OnExecuteTask>>(),
            Arg.Any<Guid?>(),
            Arg.Any<TaskTrigger>(),
            Arg.Any<TaskExecutionOrigin>(),
            Arg.Any<ScriptContext>(),
            Arg.Any<CancellationToken>());
    }

    // ─── Instance-level endpoint (GetFunctionByInstanceAsync) ───────────────────

    [Fact]
    public async Task ByInstance_DomainScope_NotInFlow_Succeeds()
    {
        var instance = SetupInstance();
        SetupFlow(declareFunction: false);
        SetupFunction(TaskScope.Domain);

        var result = await _service.GetFunctionByInstanceAsync(
            FunctionKey, TestFlow, TestDomain, instance.Id.ToString());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ByInstance_InstanceScope_Succeeds()
    {
        var instance = SetupInstance();
        SetupFlow(declareFunction: false);
        SetupFunction(TaskScope.Instance);

        var result = await _service.GetFunctionByInstanceAsync(
            FunctionKey, TestFlow, TestDomain, instance.Id.ToString());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ByInstance_FlowScope_DeclaredInFlow_Succeeds()
    {
        var instance = SetupInstance();
        SetupFlow(declareFunction: true);
        SetupFunction(TaskScope.Flow);

        var result = await _service.GetFunctionByInstanceAsync(
            FunctionKey, TestFlow, TestDomain, instance.Id.ToString());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ByInstance_FlowScope_NotDeclaredInFlow_Returns403()
    {
        var instance = SetupInstance();
        SetupFlow(declareFunction: false);
        SetupFunction(TaskScope.Flow);

        var result = await _service.GetFunctionByInstanceAsync(
            FunctionKey, TestFlow, TestDomain, instance.Id.ToString());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FunctionScopeNotSatisfied);
    }

    [Fact]
    public async Task ByInstance_InstanceNotFound_ReturnsInstanceNotFound()
    {
        _instanceRepository
            .FindByIdentifierAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((Instance?)null);

        var result = await _service.GetFunctionByInstanceAsync(
            FunctionKey, TestFlow, TestDomain, "missing-key");

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.NotFoundInstanceData);
    }

    // ─── Read-through cache ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetFunctionByKeyAsync_CacheHit_ReturnsCached_WithoutExecutingTasks()
    {
        SetupCachedFunction();
        var cached = JsonSerializer.SerializeToElement(
            new FunctionResponseOutput { Data = new Dictionary<string, object?> { ["v"] = 1 }, StatusCode = 200 },
            JsonSerializerConstants.JsonOptions);
        _cacheGateway
            .GetAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(new CacheGetResult(CacheOk: true, Hit: true, Value: cached));

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.StatusCode.ShouldBe(200);
        // On a hit the tasks are not executed and nothing is written back.
        await _taskCoordinator.DidNotReceive().ExecuteAsync(
            Arg.Any<IEnumerable<OnExecuteTask>>(), Arg.Any<Guid?>(), Arg.Any<TaskTrigger>(),
            Arg.Any<TaskExecutionOrigin>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>());
        await _cacheGateway.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFunctionByKeyAsync_CacheMiss_ExecutesTasks_AndWritesBack()
    {
        SetupCachedFunction();
        _cacheGateway
            .GetAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(new CacheGetResult(CacheOk: true, Hit: false, Value: default));
        _cacheGateway
            .SetAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<int?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain);

        result.IsSuccess.ShouldBeTrue();
        await _taskCoordinator.Received(1).ExecuteAsync(
            Arg.Any<IEnumerable<OnExecuteTask>>(), Arg.Any<Guid?>(), Arg.Any<TaskTrigger>(),
            Arg.Any<TaskExecutionOrigin>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>());
        await _cacheGateway.Received(1).SetAsync(
            "fn:test", Arg.Any<object?>(), Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFunctionByKeyAsync_WithGeneration_FoldsStampIntoKey()
    {
        SetupCachedFunction(generationKey: "dcs:gen");
        // Generation stamp read = 8 → cache key becomes "fn:test:g:8".
        _cacheGateway
            .GetAsync("dcs:gen", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(new CacheGetResult(CacheOk: true, Hit: true, Value: JsonSerializer.SerializeToElement(8)));
        _cacheGateway
            .GetAsync("fn:test:g:8", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(new CacheGetResult(CacheOk: true, Hit: false, Value: default));
        _cacheGateway
            .SetAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<int?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain);

        result.IsSuccess.ShouldBeTrue();
        // The response is cached under the generation-folded key.
        await _cacheGateway.Received(1).SetAsync(
            "fn:test:g:8", Arg.Any<object?>(), Arg.Any<int?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFunctionByKeyAsync_KeyExpressionFails_BypassesCache_AndRunsFunction()
    {
        SetupCachedFunction(keyExpressionCode: "varyKey(context)");
        // A failing key expression must NOT 500 the endpoint — it bypasses the cache and runs the function.
        _keyEvaluator
            .Evaluate(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>())
            .Returns(Result<string>.Fail(Error.Failure("expr.fail", "boom")));

        var result = await _service.GetFunctionByKeyAsync(FunctionKey, TestDomain);

        result.IsSuccess.ShouldBeTrue();
        // Function executed; cache never touched.
        await _taskCoordinator.Received(1).ExecuteAsync(
            Arg.Any<IEnumerable<OnExecuteTask>>(), Arg.Any<Guid?>(), Arg.Any<TaskTrigger>(), Arg.Any<TaskExecutionOrigin>(),
            Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>());
        await _cacheGateway.DidNotReceive().GetAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>());
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private void SetupCachedFunction(string? generationKey = null, string? keyExpressionCode = null)
    {
        var task = OnExecuteTask.Create(
            1,
            new Reference("my-task", TestDomain, "sys-tasks", TestVersion),
            ScriptCode.FromNative(string.Empty));
        var keyExpression = keyExpressionCode is null
            ? null
            : ScriptCode.FromNative(keyExpressionCode, "dynamicExpresso");
        var function = new Function(
            TaskScope.Domain, task,
            cache: new FunctionCache(
                key: "fn:test", ttlInSeconds: 300, generationKey: generationKey, keyExpression: keyExpression));
        function.SetReference(new Reference(FunctionKey, TestDomain, "sys-functions", TestVersion));

        _componentCacheStore
            .GetFunctionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Function>.Ok(function));
    }

    private void SetupFunction(TaskScope scope, List<string>? verbs = null)
    {
        var task = OnExecuteTask.Create(
            1,
            new Reference("my-task", TestDomain, "sys-tasks", TestVersion),
            ScriptCode.FromNative(string.Empty));
        var function = new Function(scope, task, verbs: verbs);
        function.SetReference(new Reference(FunctionKey, TestDomain, "sys-functions", TestVersion));

        _componentCacheStore
            .GetFunctionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Function>.Ok(function));
    }

    private Instance SetupInstance()
    {
        var instance = Instance.Create(Guid.NewGuid(), TestFlow, TestVersion, "test-key");
        _instanceRepository
            .FindByIdentifierAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(instance);
        return instance;
    }

    private void SetupFlow(bool declareFunction)
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestFlow, TestDomain, "sys-flows", TestVersion));
        workflow.SetType("F");
        if (declareFunction)
            workflow.AddFunction(new Reference(FunctionKey, TestDomain, "sys-functions", TestVersion));

        _componentCacheStore
            .GetFlowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));
    }
}
