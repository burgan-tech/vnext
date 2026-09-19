using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Invocation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// The dispatcher is the single place the router's verdict is acted on. Everything that invokes a
/// prepared binding — five executors and the function response cache — goes through here, so the
/// local/remote decision cannot be spelled six slightly different ways.
/// </summary>
/// <remarks>
/// This namespace has both <c>BBT.Workflow.Execution</c> (for <see cref="TaskTypes"/>) and
/// <c>BBT.Workflow.Tasks</c> in scope; both declare identically-named
/// <c>TaskEnvelope</c>/<c>TaskInvocationResult</c>/<c>TaskTraceContext</c> twins, so every
/// unqualified reference below to one of those three is fully qualified with
/// <c>BBT.Workflow.Tasks.</c> to avoid CS0104 — the dispatcher works with the orchestrator-side
/// family, never the wire-side one.
/// </remarks>
public sealed class TaskInvocationDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_LocalDecision_UsesTheLocalInvoker()
    {
        var harness = new Harness(ExecutionMode.Local);

        var result = await harness.DispatchAsync();

        result.IsSuccess.ShouldBeTrue();
        harness.LocalBindings.Count.ShouldBe(1);
        await harness.RemoteInvoker.DidNotReceive().InvokeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<BBT.Workflow.Tasks.TaskEnvelope>(),
            Arg.Any<BBT.Workflow.Tasks.TaskTraceContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_RemoteDecision_UsesTheExecutionService()
    {
        var harness = new Harness(ExecutionMode.Remote);

        var result = await harness.DispatchAsync();

        result.IsSuccess.ShouldBeTrue();
        harness.LocalBindings.Count.ShouldBe(0);
        await harness.RemoteInvoker.Received(1).InvokeAsync(
            TaskTypes.Http, "probe", Arg.Any<BBT.Workflow.Tasks.TaskEnvelope>(),
            Arg.Any<BBT.Workflow.Tasks.TaskTraceContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_LocalDecisionButNoInvokerRegistered_FallsBackToRemote()
    {
        // Belt and braces: the router already applies this gate, but the dispatcher is what would
        // actually dereference a missing invoker, so it must not depend on the router being right.
        var harness = new Harness(ExecutionMode.Local, registerLocalInvoker: false);

        var result = await harness.DispatchAsync();

        result.IsSuccess.ShouldBeTrue();
        await harness.RemoteInvoker.Received(1).InvokeAsync(
            TaskTypes.Http, "probe", Arg.Any<BBT.Workflow.Tasks.TaskEnvelope>(),
            Arg.Any<BBT.Workflow.Tasks.TaskTraceContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_LocalTransportFailure_IsAFailedResultNotAFailedDispatch()
    {
        // The error boundary must see the same shape on both paths: a transport failure is a failed
        // RESULT (Result.Ok carrying IsSuccess=false), never Result.Fail, which is reserved for
        // pre-flight errors like a malformed envelope.
        var harness = new Harness(ExecutionMode.Local, localResult:
            BBT.Workflow.Tasks.TaskInvocationResult.Failure(error: "connection refused", taskType: TaskTypes.Http));

        var result = await harness.DispatchAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.ErrorMessage.ShouldBe("connection refused");
    }

    [Fact]
    public async Task DispatchAsync_LocalInvocationExceedsTheLocalTimeout_ReturnsA408ResultNotAnException()
    {
        // F1: before this, a local invocation carried the caller's token straight through with no
        // deadline of its own. A local invoker whose underlying core propagates cancellation (the
        // real behaviour of CacheAsideInvocation's own state-store step) must come back as a failed
        // RESULT, not an unhandled OperationCanceledException, when OUR timer — not the caller's — fires.
        var harness = new Harness(ExecutionMode.Local, hangUntilCancelled: true, localInvocationTimeoutSeconds: 1);

        var result = await harness.DispatchAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.StatusCode.ShouldBe(408);
    }

    [Fact]
    public async Task DispatchAsync_CallerCancelsBeforeTheLocalTimeout_PropagatesTheCancellation()
    {
        // The caller's own token firing must still propagate as an exception, exactly as it did
        // before this timeout existed — only OUR timer produces a result.
        var harness = new Harness(ExecutionMode.Local, hangUntilCancelled: true, localInvocationTimeoutSeconds: 60);
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => harness.DispatchAsync(callerCts.Token));
    }

    private sealed class Harness
    {
        public IRemoteInvokerService RemoteInvoker { get; } = Substitute.For<IRemoteInvokerService>();

        public List<JsonElement> LocalBindings { get; } = [];

        private readonly TaskInvocationDispatcher _dispatcher;
        private readonly HttpTask _task;

        public Harness(
            ExecutionMode mode,
            bool registerLocalInvoker = true,
            BBT.Workflow.Tasks.TaskInvocationResult? localResult = null,
            bool hangUntilCancelled = false,
            int localInvocationTimeoutSeconds = 60)
        {
            _task = WorkflowTaskFactory.CreateHttpTask("probe");

            RemoteInvoker.InvokeAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<BBT.Workflow.Tasks.TaskEnvelope>(),
                    Arg.Any<BBT.Workflow.Tasks.TaskTraceContext>(), Arg.Any<CancellationToken>())
                .Returns(Result<BBT.Workflow.Tasks.TaskInvocationResult>.Ok(BBT.Workflow.Tasks.TaskInvocationResult.Success()));

            var router = Substitute.For<ITaskInvocationRouter>();
            router.Resolve(Arg.Any<WorkflowTask>(), TaskTypes.Http)
                .Returns(new TaskInvocationDecision(mode, "test"));

            var registry = Substitute.For<ILocalTaskInvokerRegistry>();
            registry.Get(TaskTypes.Http).Returns(registerLocalInvoker
                ? new RecordingLocalInvoker(LocalBindings, localResult, hangUntilCancelled)
                : null);

            var options = Options.Create(new TaskInvocationOptions
            {
                LocalInvocationTimeoutSeconds = localInvocationTimeoutSeconds
            });

            _dispatcher = new TaskInvocationDispatcher(
                router, registry, RemoteInvoker, options, NullLogger<TaskInvocationDispatcher>.Instance);
        }

        public Task<Result<BBT.Workflow.Tasks.TaskInvocationResult>> DispatchAsync(
            CancellationToken cancellationToken = default)
        {
            var envelope = new BBT.Workflow.Tasks.TaskEnvelope
            {
                TaskType = TaskTypes.Http,
                TaskKey = "probe",
                Binding = JsonSerializer.SerializeToElement(new { url = "https://x.local" })
            };

            return _dispatcher.DispatchAsync(
                _task, TaskTypes.Http, envelope, new BBT.Workflow.Tasks.TaskTraceContext(), cancellationToken);
        }

        private sealed class RecordingLocalInvoker(
            List<JsonElement> bindings,
            BBT.Workflow.Tasks.TaskInvocationResult? result,
            bool hangUntilCancelled = false) : ILocalTaskInvoker
        {
            public string TaskType => TaskTypes.Http;

            public async Task<BBT.Workflow.Tasks.TaskInvocationResult> InvokeAsync(
                string? taskKey, JsonElement binding, BBT.Workflow.Tasks.TaskTraceContext? traceContext,
                CancellationToken cancellationToken = default)
            {
                bindings.Add(binding.Clone());

                if (hangUntilCancelled)
                {
                    // Mirrors CacheAsideInvocation's own state-store step, the one local invocation
                    // core that actually propagates cancellation instead of swallowing it into a
                    // failed result — see StateStoreInvocation/HttpTaskInvocation/DaprServiceInvocation
                    // for the contrasting swallow-into-result behaviour.
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return result ?? BBT.Workflow.Tasks.TaskInvocationResult.Success();
            }
        }
    }
}
