using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks;

/// <summary>
/// Pins <see cref="TaskExecutorBase{TTask}.GetOrCompileMappingAsync{T}"/>: the engine is asked to
/// compile a given (mapping, target-type) pair exactly ONCE per <see cref="TaskExecutorContext"/>
/// (one task execution), regardless of how many phases (PrepareInput/ProcessOutput/Invoke) ask for
/// it — but each phase still gets a FRESH instance, exactly as calling
/// <see cref="IScriptEngine.CompileToInstanceAsync{T}(ScriptCode, ScriptSettings?, System.Collections.Generic.IEnumerable{Microsoft.CodeAnalysis.MetadataReference}?, System.Collections.Generic.IEnumerable{string}?, CancellationToken)"/>
/// per phase always did. A second, distinct <see cref="TaskExecutorContext"/> (a new task execution)
/// must NOT reuse the first context's memo — the memo is context-scoped, not process-wide.
/// </summary>
public class TaskExecutorMappingMemoTests
{
    private sealed class MemoProbeExecutor(Microsoft.Extensions.Logging.ILogger logger, IScriptEngine scriptEngine)
        : TaskExecutorBase<ScriptTask>(logger)
    {
        public override TaskType TaskType => TaskType.Script;

        public TaskInvocationResult InvocationResult { get; init; } = new() { IsSuccess = true };

        public Task<IMapping> CallGetOrCompile(TaskExecutorContext context)
            => GetOrCompileMappingAsync<IMapping>(scriptEngine, context, CancellationToken.None);

        protected override Task<Result<TaskInvocationResult>> InvokeAsync(
            ScriptTask task, TaskExecutorContext context, CancellationToken ct)
            => Task.FromResult(Result<TaskInvocationResult>.Ok(InvocationResult));
    }

    [Fact]
    public async Task SamePipeline_InputAndOutput_CompileOnce_ButGetFreshInstances()
    {
        var engine = new Mock<IScriptEngine>();
        var created = 0;
        engine.Setup(e => e.CompileToFactoryAsync<IMapping>(
                It.IsAny<ScriptCode>(), It.IsAny<ScriptSettings?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                created++;
                return Mock.Of<IMapping>();
            });

        var context = TestTaskContexts.ScriptTaskWithMapping();

        var probe = new MemoProbeExecutor(NullLogger.Instance, engine.Object);
        var a = await probe.CallGetOrCompile(context);
        var b = await probe.CallGetOrCompile(context);

        engine.Verify(e => e.CompileToFactoryAsync<IMapping>(
            It.IsAny<ScriptCode>(), It.IsAny<ScriptSettings?>(), It.IsAny<CancellationToken>()), Times.Once);
        a.ShouldNotBeSameAs(b);   // every phase still gets a fresh instance
        created.ShouldBe(2);

        var context2 = TestTaskContexts.ScriptTaskWithMapping();
        await probe.CallGetOrCompile(context2);
        engine.Verify(e => e.CompileToFactoryAsync<IMapping>(
            It.IsAny<ScriptCode>(), It.IsAny<ScriptSettings?>(), It.IsAny<CancellationToken>()), Times.Exactly(2)); // memo is context-scoped
    }

    [Fact]
    public async Task ExecuteAsync_MaterializesRawInvocationResultOnce_ForJournalWrite()
    {
        var invocationResult = TaskInvocationResult.Success(
            data: new { CustomerId = 42 },
            statusCode: 202,
            taskType: "Script");
        var context = TestTaskContexts.ScriptTaskWithoutMapping();
        var probe = new MemoProbeExecutor(NullLogger.Instance, Mock.Of<IScriptEngine>())
        {
            InvocationResult = invocationResult
        };

        var result = await probe.ExecuteAsync(context);

        result.IsSuccess.ShouldBeTrue();
        context.RawInvocationResult.ShouldNotBeNull();
        context.RawInvocationResult.Json.ShouldBe(
            System.Text.Json.JsonSerializer.Serialize(invocationResult, JsonSerializerConstants.JsonOptions));
    }
}
