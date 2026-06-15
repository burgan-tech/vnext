using System;
using System.Collections.Generic;
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
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
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
    private readonly FunctionAppService _service;

    private readonly IServiceProvider _ambientServiceProvider;
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public FunctionAppServiceScopeTests()
    {
        _instanceRepository = Substitute.For<IInstanceRepository>();
        _componentCacheStore = Substitute.For<IComponentCacheStore>();
        _taskCoordinator = Substitute.For<ITaskCoordinator>();

        // Ambient provider needed by PostSharp UnitOfWork interception.
        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager
            .BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockUoW));
        var services = new ServiceCollection();
        services.AddSingleton(mockUoWManager);
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
        builder.BuildAsync(Arg.Any<CancellationToken>()).Returns(scriptContext);

        var scriptContextFactory = Substitute.For<IScriptContextFactory>();
        scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);

        _taskCoordinator
            .ExecuteAsync(
                Arg.Any<IEnumerable<OnExecuteTask>>(),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
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
            currentUser: Substitute.For<ICurrentUser>(),
            transitionAuthorizationManager: Substitute.For<ITransitionAuthorizationManager>());
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

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private void SetupFunction(TaskScope scope)
    {
        var task = OnExecuteTask.Create(
            1,
            new Reference("my-task", TestDomain, "sys-tasks", TestVersion),
            ScriptCode.FromNative(string.Empty));
        var function = new Function(scope, task);
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
