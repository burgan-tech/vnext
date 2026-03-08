using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Domain;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Context;

/// <summary>
/// Unit tests for ContextRefresher
/// Tests context refresh operations for transition execution
/// </summary>
public class ContextRefresherTests
{
    private readonly Mock<IInstanceRepository> _mockInstanceRepository;
    private readonly ContextRefresher _refresher;

    public ContextRefresherTests()
    {
        _mockInstanceRepository = new Mock<IInstanceRepository>();
        _refresher = new ContextRefresher(_mockInstanceRepository.Object);
    }

    #region RefreshAsync Tests

    [Fact]
    public async Task RefreshAsync_ShouldRefreshInstanceAndUpdateContext()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var currentState = "state1";
        
        var instance = CreateMockInstance(instanceId, workflowKey, currentState);
        var workflow = CreateMockWorkflow(workflowKey, currentState);
        var context = CreateExecutionContext(instanceId, workflow, currentState);

        _mockInstanceRepository
            .Setup(x => x.GetAsync(instanceId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        // Act
        var result = await _refresher.RefreshAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.ShouldBe(instance);
        context.Current.Key.ShouldBe(currentState);
        context.Target.ShouldBeNull();
        
        _mockInstanceRepository.Verify(
            x => x.GetAsync(instanceId, true, It.IsAny<CancellationToken>()), 
            Times.Once);
    }

    [Fact]
    public async Task RefreshAsync_WhenStateNotFound_ShouldReturnFailure()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var currentState = "invalid-state";
        
        var instance = CreateMockInstance(instanceId, workflowKey, currentState);
        var workflow = CreateMockWorkflow(workflowKey, "valid-state");
        var context = CreateExecutionContext(instanceId, workflow, "valid-state");

        _mockInstanceRepository
            .Setup(x => x.GetAsync(instanceId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        // Act
        var result = await _refresher.RefreshAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldNotBe(default);
    }

    [Fact]
    public async Task RefreshAsync_ShouldUpdateConcurrencyToken()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var currentState = "state1";
        var newConcurrencyToken = "new-token";
        
        var instance = CreateMockInstance(instanceId, workflowKey, currentState);
        
        var workflow = CreateMockWorkflow(workflowKey, currentState);
        var context = CreateExecutionContext(instanceId, workflow, currentState);
        context.ConcurrencyToken = "old-token";

        _mockInstanceRepository
            .Setup(x => x.GetAsync(instanceId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        // Act
        var result = await _refresher.RefreshAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.ConcurrencyToken.ShouldBe(newConcurrencyToken);
    }

    [Fact]
    public async Task RefreshAsync_ShouldResetTargetState()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var currentState = "state1";
        
        var instance = CreateMockInstance(instanceId, workflowKey, currentState);
        var workflow = CreateMockWorkflow(workflowKey, currentState);
        var context = CreateExecutionContext(instanceId, workflow, currentState);
        
        // Set a target state that should be reset
        context.Target = workflow.GetState("state2").Value;

        _mockInstanceRepository
            .Setup(x => x.GetAsync(instanceId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);

        // Act
        var result = await _refresher.RefreshAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Target.ShouldBeNull();
    }

    [Fact]
    public async Task RefreshAsync_WithCancellation_ShouldPropagateCancellation()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var workflow = CreateMockWorkflow("test-workflow", "state1");
        var context = CreateExecutionContext(instanceId, workflow, "state1");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockInstanceRepository
            .Setup(x => x.GetAsync(instanceId, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _refresher.RefreshAsync(context, cts.Token)
        );
    }

    #endregion

    #region Helper Methods

    private Instance CreateMockInstance(Guid instanceId, string workflowKey, string currentState)
    {
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0", workflowKey);
        
        // Set current state
        typeof(Instance)
            .GetProperty(nameof(Instance.CurrentState))!
            .SetValue(instance, currentState);
        
        return instance;
    }

    private Definitions.Workflow CreateMockWorkflow(string key, params string[] stateKeys)
    {
        var json = $$"""
        {
            "type": "F",
            "timeout": null,
            "labels": [],
            "functions": [],
            "features": [],
            "states": [
                {{string.Join(",\n                ", stateKeys.Select(sk => $@"{{""key"": ""{sk}"", ""type"": ""P"", ""transitions"": []}}"))}}
            ],
            "sharedTransitions": [],
            "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "{{stateKeys.FirstOrDefault() ?? "init"}}", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;
        
        var options = new System.Text.Json.JsonSerializerOptions 
        { 
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        
        workflow.SetReference(new Reference(key, "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }

    private TransitionExecutionContext CreateExecutionContext(
        Guid instanceId, 
        Definitions.Workflow workflow, 
        string currentStateKey)
    {
        var instance = CreateMockInstance(instanceId, workflow.Key, currentStateKey);
        var currentState = workflow.GetState(currentStateKey).Value!;
        
        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = "test-domain",
            WorkflowKey = workflow.Key,
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = currentState,
            Instance = instance,
            Data = instance.Data,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    #endregion
}

