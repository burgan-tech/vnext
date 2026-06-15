using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for <see cref="ResourceLockStep"/>.
/// Validates Acquire/Release/Extend outcomes and error handling after code review fixes.
/// </summary>
public class ResourceLockStepTests
{
    private const string TestResourceKey = "seat:concert1:A1";
    private const int DefaultTtl = 300;

    private readonly IScriptEngine _scriptEngine;
    private readonly IScriptContextFactory _scriptContextFactory;
    private readonly IInstanceRepository _instanceRepository;
    private readonly IResourceLockService _resourceLockService;
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;
    private readonly ResourceLockStep _step;

    public ResourceLockStepTests()
    {
        _scriptEngine = Substitute.For<IScriptEngine>();
        _scriptContextFactory = Substitute.For<IScriptContextFactory>();
        _instanceRepository = Substitute.For<IInstanceRepository>();
        _resourceLockService = Substitute.For<IResourceLockService>();
        _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();

        _step = new ResourceLockStep(
            _scriptEngine,
            _scriptContextFactory,
            _instanceRepository,
            _resourceLockService,
            _runtimeInfoProvider);
    }

    [Fact]
    public void Order_ShouldBeResourceLock()
    {
        _step.Order.ShouldBe(LifecycleOrder.ResourceLock);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoResourceLock_ShouldContinue()
    {
        var context = CreateContext(resourceLock: null);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.StopPipeline.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Acquire_WhenSuccessful_ShouldContinue()
    {
        var context = CreateContext(ResourceLockAction.Acquire);
        SetupScriptEngine(TestResourceKey);
        _resourceLockService
            .AcquireAsync(TestResourceKey, Arg.Any<string>(), DefaultTtl, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.StopPipeline.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Acquire_WhenConflict_ShouldFail()
    {
        var context = CreateContext(ResourceLockAction.Acquire);
        SetupScriptEngine(TestResourceKey);
        _resourceLockService
            .AcquireAsync(TestResourceKey, Arg.Any<string>(), DefaultTtl, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ResourceLockConflict);
    }

    [Fact]
    public async Task ExecuteAsync_Release_WhenSuccessful_ShouldContinue()
    {
        var context = CreateContext(ResourceLockAction.Release);
        SetupScriptEngine(TestResourceKey);
        _resourceLockService
            .ReleaseAsync(TestResourceKey, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.StopPipeline.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Release_WhenFailed_ShouldFail()
    {
        var context = CreateContext(ResourceLockAction.Release);
        SetupScriptEngine(TestResourceKey);
        _resourceLockService
            .ReleaseAsync(TestResourceKey, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ResourceLockReleaseFailed);
    }

    [Fact]
    public async Task ExecuteAsync_Extend_WhenSuccessful_ShouldContinue()
    {
        var context = CreateContext(ResourceLockAction.Extend);
        SetupScriptEngine(TestResourceKey);
        _resourceLockService
            .ExtendAsync(TestResourceKey, Arg.Any<string>(), DefaultTtl, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.StopPipeline.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Extend_WhenFailed_ShouldFail()
    {
        var context = CreateContext(ResourceLockAction.Extend);
        SetupScriptEngine(TestResourceKey);
        _resourceLockService
            .ExtendAsync(TestResourceKey, Arg.Any<string>(), DefaultTtl, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ResourceLockConflict);
    }

    [Fact]
    public async Task ExecuteAsync_WhenKeyExpressionReturnsEmpty_ShouldFail()
    {
        var context = CreateContext(ResourceLockAction.Acquire);
        SetupScriptEngine(string.Empty);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ResourceLockKeyEmpty);
    }

    [Fact]
    public async Task ExecuteAsync_WhenKeyExpressionThrows_ShouldFail()
    {
        var context = CreateContext(ResourceLockAction.Acquire);

        var mapping = Substitute.For<ITransitionMapping>();
        mapping.Handler(Arg.Any<ScriptContext>())
            .ReturnsForAnyArgs<Task<dynamic>>(_ => throw new InvalidOperationException("script error"));

        _scriptEngine
            .CompileToInstanceAsync<ITransitionMapping>(Arg.Any<string>())
            .ReturnsForAnyArgs(Task.FromResult(mapping));

        SetupScriptContextFactory();

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ResourceLockKeyResolutionFailed);
    }

    #region Helpers

    private void SetupScriptEngine(string keyResult)
    {
        var mapping = Substitute.For<ITransitionMapping>();
        mapping.Handler(Arg.Any<ScriptContext>())
            .ReturnsForAnyArgs(Task.FromResult<dynamic>(keyResult));

        _scriptEngine
            .CompileToInstanceAsync<ITransitionMapping>(Arg.Any<string>())
            .ReturnsForAnyArgs(Task.FromResult(mapping));

        SetupScriptContextFactory();
    }

    private void SetupScriptContextFactory()
    {
        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithWorkflow(Arg.Any<Definitions.Workflow>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance>()).Returns(builder);
        builder.WithTransition(Arg.Any<Transition>()).Returns(builder);
        builder.WithBody(Arg.Any<object>()).Returns(builder);
        builder.WithHeaders(Arg.Any<Dictionary<string, string?>>()).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>())
            .Returns(new ScriptContext(Substitute.For<ILogger<ScriptContext>>()));

        _scriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);
    }

    private static TransitionExecutionContext CreateContext(ResourceLockAction action)
    {
        var lockDef = new ResourceLockDefinition(
            ScriptCode.FromNative("return \"seat:concert1:A1\";"),
            action,
            DefaultTtl);

        return CreateContextCore(lockDef);
    }

    private static TransitionExecutionContext CreateContext(ResourceLockDefinition? resourceLock)
    {
        return CreateContextCore(resourceLock);
    }

    private static TransitionExecutionContext CreateContextCore(ResourceLockDefinition? resourceLock)
    {
        var instanceId = Guid.NewGuid();
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";

        var transitionJson = resourceLock is null
            ? """{"key": "tr1", "from": "state1", "target": "state2", "triggerType": "Manual", "versionStrategy": "Patch"}"""
            : $$"""
              {
                "key": "tr1", "from": "state1", "target": "state2",
                "triggerType": "Manual", "versionStrategy": "Patch",
                "resourceLock": {
                  "keyExpression": { "location": "inline", "code": "dummy", "type": "L", "encoding": "NAT" },
                  "action": "{{resourceLock.Action}}",
                  "ttlSeconds": {{resourceLock.TtlSeconds}},
                  "onConflict": "{{resourceLock.OnConflict}}"
                }
              }
              """;

        var workflowJson = $$"""
        {
            "type": "F", "timeout": null, "labels": [], "functions": [], "features": [],
            "states": [
                { "key": "state1", "stateType": "Intermediate", "transitions": [{{transitionJson}}] },
                { "key": "state2", "stateType": "Intermediate", "transitions": [] }
            ],
            "sharedTransitions": [], "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(workflowJson, options)!;
        workflow.SetReference(new Reference(workflowKey, domain, "sys-flows", "1.0.0"));

        var state = workflow.GetState("state1").Value!;
        var transition = state.Transitions.First(t => t.Key == "tr1");
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "tr1",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Target = workflow.GetState("state2").Value!,
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    #endregion
}
