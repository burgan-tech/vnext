using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Domain;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Handlers;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Handlers;

/// <summary>
/// Unit tests for AutomaticTransitionHandler
/// Tests automatic transition handling including condition validation
/// </summary>
public class AutomaticTransitionHandlerTests
{
    private readonly Mock<ITaskConditionService> _mockConditionService;
    private readonly Mock<IScriptContextFactory> _mockScriptContextFactory;
    private readonly Mock<ITransitionValidationService> _mockValidationService;
    private readonly Mock<ILogger<AutomaticTransitionHandler>> _mockLogger;
    private readonly AutomaticTransitionHandler _handler;

    public AutomaticTransitionHandlerTests()
    {
        _mockConditionService = new Mock<ITaskConditionService>();
        _mockScriptContextFactory = new Mock<IScriptContextFactory>();
        _mockValidationService = new Mock<ITransitionValidationService>();
        _mockLogger = new Mock<ILogger<AutomaticTransitionHandler>>();

        _handler = new AutomaticTransitionHandler(
            _mockValidationService.Object,
            _mockLogger.Object);
    }

    #region CanHandle Tests

    [Fact]
    public void CanHandle_WithAutomaticTrigger_ShouldReturnTrue()
    {
        // Act
        var result = _handler.CanHandle(TriggerType.Automatic);

        // Assert
        result.ShouldBeTrue();
    }

    [Theory]
    [InlineData(TriggerType.Manual)]
    [InlineData(TriggerType.Scheduled)]
    [InlineData(TriggerType.Event)]
    public void CanHandle_WithNonAutomaticTrigger_ShouldReturnFalse(TriggerType triggerType)
    {
        // Act
        var result = _handler.CanHandle(triggerType);

        // Assert
        result.ShouldBeFalse();
    }

    #endregion

    #region PreHandleAsync Tests

    [Fact]
    public async Task PreHandleAsync_WithValidCondition_ShouldCompleteSuccessfully()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        // Set Rule using reflection since it's private setter
        var scriptCode = new ScriptCode("inline", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("true")));
        typeof(Transition)
            .GetProperty(nameof(Transition.Rule))!
            .SetValue(context.Transition, scriptCode);

        SetupSuccessfulValidation();
        SetupScriptContextFactory();
        SetupConditionService(true);

        // Act
        await _handler.PreHandleAsync(context, CancellationToken.None);

        // Assert - No exception thrown
        _mockConditionService.Verify(
            x => x.ExecuteConditionAsync(
                It.IsAny<ScriptCode>(),
                It.IsAny<ScriptContext>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
    
    [Fact]
    public async Task PreHandleAsync_WithoutCondition_ShouldCompleteSuccessfully()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        // Rule is null by default

        SetupSuccessfulValidation();

        // Act
        await _handler.PreHandleAsync(context, CancellationToken.None);

        // Assert
        _mockConditionService.Verify(
            x => x.ExecuteConditionAsync(
                It.IsAny<ScriptCode>(),
                It.IsAny<ScriptContext>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PreHandleAsync_ShouldLogDebugMessages()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        var scriptCode = new ScriptCode("inline", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("true")));
        typeof(Transition)
            .GetProperty(nameof(Transition.Rule))!
            .SetValue(context.Transition, scriptCode);

        SetupSuccessfulValidation();
        SetupScriptContextFactory();
        SetupConditionService(true);

        // Act
        await _handler.PreHandleAsync(context, CancellationToken.None);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Validating automatic transition")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task PreHandleAsync_WithCachedScriptContext_ShouldReuseContext()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        var scriptCode = new ScriptCode("inline", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("true")));
        typeof(Transition)
            .GetProperty(nameof(Transition.Rule))!
            .SetValue(context.Transition, scriptCode);

        var scriptContext = CreateMockScriptContext();
        context.Cache["ScriptContext"] = scriptContext;

        SetupSuccessfulValidation();
        SetupConditionService(true);

        // Act
        await _handler.PreHandleAsync(context, CancellationToken.None);

        // Assert
        _mockScriptContextFactory.Verify(
            x => x.NewBuilder(It.IsAny<IInstanceRepository>()),
            Times.Never);
    }

    #endregion

    #region PostHandleAsync Tests

    [Fact]
    public async Task PostHandleAsync_ShouldCompleteSuccessfully()
    {
        // Arrange
        var context = CreateValidTransitionContext();

        // Act
        await _handler.PostHandleAsync(context, CancellationToken.None);

        // Assert - No exception thrown
    }

    [Fact]
    public async Task PostHandleAsync_ShouldLogExecutionInformation()
    {
        // Arrange
        var context = CreateValidTransitionContext();

        // Act
        await _handler.PostHandleAsync(context, CancellationToken.None);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Automatic transition executed")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task PostHandleAsync_ShouldIncludeExecutionChainInformation()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        var executionChainId = Guid.NewGuid().ToString("N");
        var chainDepth = 5;

        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.ExecutionChainId))!
            .SetValue(context, executionChainId);

        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.ChainDepth))!
            .SetValue(context, chainDepth);

        // Act
        await _handler.PostHandleAsync(context, CancellationToken.None);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => 
                    v.ToString()!.Contains(executionChainId) &&
                    v.ToString()!.Contains(chainDepth.ToString())),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region Helper Methods

    private void SetupSuccessfulValidation()
    {
        _mockValidationService
            .Setup(x => x.ValidateAsync(It.IsAny<TransitionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());
    }

    private void SetupScriptContextFactory()
    {
        var scriptContext = CreateMockScriptContext();
        var mockBuilder = new Mock<IScriptContextBuilder>();

        mockBuilder
            .Setup(x => x.WithWorkflow(It.IsAny<Definitions.Workflow>()))
            .Returns(mockBuilder.Object);
        mockBuilder
            .Setup(x => x.WithInstance(It.IsAny<Instance>()))
            .Returns(mockBuilder.Object);
        mockBuilder
            .Setup(x => x.WithTransition(It.IsAny<Transition>()))
            .Returns(mockBuilder.Object);
        mockBuilder
            .Setup(x => x.WithBody(It.IsAny<object>()))
            .Returns(mockBuilder.Object);
        mockBuilder
            .Setup(x => x.WithHeaders(It.IsAny<System.Collections.Generic.Dictionary<string, string?>>()))
            .Returns(mockBuilder.Object);
        mockBuilder
            .Setup(x => x.BuildAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(scriptContext);

        _mockScriptContextFactory
            .Setup(x => x.NewBuilder(It.IsAny<IInstanceRepository>()))
            .Returns(mockBuilder.Object);
    }

    private void SetupConditionService(bool conditionResult)
    {
        _mockConditionService
            .Setup(x => x.ExecuteConditionAsync(
                It.IsAny<ScriptCode>(),
                It.IsAny<ScriptContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Ok(conditionResult));
    }

    private TransitionExecutionContext CreateValidTransitionContext()
    {
        return CreateTransitionContextWithChainDepth(0);
    }

    private TransitionExecutionContext CreateTransitionContextWithChainDepth(int chainDepth)
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";
        var transitionKey = "test-transition";

        var workflow = HandlerTestHelpers.CreateMockWorkflow(workflowKey, domain);
        var instance = HandlerTestHelpers.CreateMockInstance(instanceId, workflowKey);
        var state = workflow.GetState("state1").Value!;
        var transition = HandlerTestHelpers.CreateMockTransition(transitionKey, "state1", TriggerType.Automatic);

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = transitionKey,
            Trigger = TriggerType.Automatic,
            Actor = ExecutionActor.System,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            ChainDepth = chainDepth,
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = transition,
            Instance = instance,
            Data = new { test = "data" },
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private ScriptContext CreateMockScriptContext()
    {
        // Create a minimal mock ScriptContext using the factory
        var mockScriptContext = new Mock<ScriptContext>(Mock.Of<ILogger<ScriptContext>>());
        return mockScriptContext.Object;
    }

    #endregion
}

