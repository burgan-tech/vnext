using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NSubstitute;
using Xunit;

namespace BBT.Workflow.Tasks;

public class TaskExecutorBaseMetricsTests
{
    // Pins: phase metrics written when the task HAS a mapping; nothing recorded without one;
    // runtime-error + rethrow on exception.
    private sealed class ProbeExecutor(Microsoft.Extensions.Logging.ILogger logger, IWorkflowMetrics metrics)
        : TaskExecutorBase<ScriptTask>(logger, metrics)
    {
        public bool ThrowOnPrepare { get; set; }
        public bool ThrowOperationCanceledOnPrepare { get; set; }
        public override TaskType TaskType => TaskType.Script;

        protected override Task<Result<ScriptResponse?>> PrepareInputAsync(
            ScriptTask task, TaskExecutorContext context, CancellationToken ct)
            => ThrowOperationCanceledOnPrepare
                ? throw new OperationCanceledException("cancelled")
                : ThrowOnPrepare
                    ? throw new InvalidOperationException("boom")
                    : Task.FromResult(Result<ScriptResponse?>.Ok(null));

        protected override Task<Result<TaskInvocationResult>> InvokeAsync(
            ScriptTask task, TaskExecutorContext context, CancellationToken ct)
            => Task.FromResult(Result<TaskInvocationResult>.Ok(new TaskInvocationResult { IsSuccess = true }));
    }

    [Fact]
    public async Task Execute_WithMapping_RecordsInputAndOutputPhaseDurations()
    {
        var metrics = new Mock<IWorkflowMetrics>();
        var executor = new ProbeExecutor(NullLogger.Instance, metrics.Object);
        var context = TestTaskContexts.ScriptTaskWithMapping();

        var result = await executor.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        metrics.Verify(m => m.RecordScriptExecutionDuration(
            "task-input", "csharp", "success", It.IsAny<double>()), Times.Once);
        metrics.Verify(m => m.RecordScriptExecutionDuration(
            "task-output", "csharp", "success", It.IsAny<double>()), Times.Once);
    }

    [Fact]
    public async Task Execute_WithoutMapping_RecordsNothing()
    {
        var metrics = new Mock<IWorkflowMetrics>();
        var executor = new ProbeExecutor(NullLogger.Instance, metrics.Object);
        var context = TestTaskContexts.ScriptTaskWithoutMapping();

        _ = await executor.ExecuteAsync(context);

        metrics.Verify(m => m.RecordScriptExecutionDuration(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
    }

    [Fact]
    public async Task Execute_PrepareThrows_RecordsRuntimeErrorAndRethrows()
    {
        var metrics = new Mock<IWorkflowMetrics>();
        var executor = new ProbeExecutor(NullLogger.Instance, metrics.Object) { ThrowOnPrepare = true };
        var context = TestTaskContexts.ScriptTaskWithMapping();

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(context));

        metrics.Verify(m => m.RecordScriptRuntimeError(
            "task-input", "csharp", nameof(InvalidOperationException)), Times.Once);
    }

    [Fact]
    public async Task Execute_PrepareThrowsOperationCanceled_PropagatesWithoutRecordingRuntimeError()
    {
        var metrics = new Mock<IWorkflowMetrics>();
        var executor = new ProbeExecutor(NullLogger.Instance, metrics.Object) { ThrowOperationCanceledOnPrepare = true };
        var context = TestTaskContexts.ScriptTaskWithMapping();

        await Assert.ThrowsAsync<OperationCanceledException>(() => executor.ExecuteAsync(context));

        metrics.Verify(m => m.RecordScriptRuntimeError(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}

/// <summary>
/// Builds realistic <see cref="TaskExecutorContext"/> fixtures for a <see cref="ScriptTask"/>, with or
/// without an <see cref="OnExecuteTask.Mapping"/> that has actual mapping code
/// (<see cref="ScriptCode.HasMappingCode"/>).
/// </summary>
internal static class TestTaskContexts
{
    public static TaskExecutorContext ScriptTaskWithMapping()
        => Build(ScriptCode.FromNative("return null;"));

    public static TaskExecutorContext ScriptTaskWithoutMapping()
        => Build(ScriptCode.FromNative(string.Empty));

    private static TaskExecutorContext Build(ScriptCode mapping)
    {
        var task = ScriptTask.Create(JsonSerializer.SerializeToElement(new { }));
        task.SetReference(new Reference("probe-script", "test", "sys-tasks", "1.0.0"));

        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(NSubstitute.Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(instance)
            .Build();

        var onExecute = OnExecuteTask.Create(1, task, mapping);

        return new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);
    }
}
