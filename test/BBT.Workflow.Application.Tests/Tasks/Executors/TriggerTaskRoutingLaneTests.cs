using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Pins the trace-lane half of trigger-task routing.
/// <para>
/// A same-domain trigger task dispatches IN-PROCESS, so whatever the target instance starts reads
/// the ambient <see cref="WorkflowTraceLane"/> — which still belongs to the CALLING instance's
/// request. Left alone, the triggered instance's transition jobs and post-commit work anchor to the
/// caller's lane and surface as siblings of the caller's own hops, with nothing in the trace tying
/// them to the task that triggered them. The local branch therefore opens a child lane anchored on
/// the task's own span.
/// </para>
/// </summary>
public sealed class TriggerTaskRoutingLaneTests : IDisposable
{
    private const string LocalDomain = "local-domain";
    private const string OtherDomain = "other-domain";

    public void Dispose() => Activity.Current = null;

    [Fact]
    public async Task SameDomain_AnchorsTheChildLaneOnTheTaskSpan()
    {
        using var callerLane = WorkflowTraceLane.Reset(
            "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-1111111111111111-01");

        using var taskSpan = StartSpan("Task.Execute.start-child");

        var executor = CreateExecutor(LocalDomain);
        string? anchorInsideLocal = null;
        string? parentLaneInsideLocal = null;

        await executor.RouteForTest(
            local: () =>
            {
                anchorInsideLocal = WorkflowTraceLane.Current;
                parentLaneInsideLocal = WorkflowTraceLane.ParentLane;
                return Task.FromResult("local");
            },
            remote: () => Task.FromResult("remote"));

        // The task's own span becomes the triggered instance's anchor: its work lands UNDER
        // Task.Execute rather than beside the caller's hops.
        anchorInsideLocal.ShouldBe(taskSpan.Id);

        // The caller's lane is retained as the parent lane, so a resume can return to it.
        parentLaneInsideLocal.ShouldBe(
            "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-1111111111111111-01");
    }

    [Fact]
    public async Task SameDomain_RestoresTheCallerLaneAfterwards()
    {
        const string callerAnchor = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-1111111111111111-01";
        using var callerLane = WorkflowTraceLane.Reset(callerAnchor);
        using var taskSpan = StartSpan("Task.Execute.start-child");

        var executor = CreateExecutor(LocalDomain);

        await executor.RouteForTest(
            local: () => Task.FromResult("local"),
            remote: () => Task.FromResult("remote"));

        // A leaked child lane would re-anchor every LATER hop of the caller's own chain onto this
        // task's span.
        WorkflowTraceLane.Current.ShouldBe(callerAnchor);
    }

    [Fact]
    public async Task CrossDomain_LeavesTheLaneUntouched()
    {
        const string callerAnchor = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-1111111111111111-01";
        using var callerLane = WorkflowTraceLane.Reset(callerAnchor);
        using var taskSpan = StartSpan("Task.Execute.start-child");

        var executor = CreateExecutor(OtherDomain);
        string? anchorInsideRemote = null;

        var result = await executor.RouteForTest(
            local: () => Task.FromResult("local"),
            remote: () =>
            {
                anchorInsideRemote = WorkflowTraceLane.Current;
                return Task.FromResult("remote");
            });

        result.ShouldBe("remote");

        // The remote branch crosses Dapr and the invoker stamps the lane into the request; entering
        // a child lane here would re-anchor it to this process's task span instead.
        anchorInsideRemote.ShouldBe(callerAnchor);
    }

    [Fact]
    public async Task EmptyTargetDomain_CountsAsSameDomain()
    {
        using var callerLane = WorkflowTraceLane.Reset(
            "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-1111111111111111-01");
        using var taskSpan = StartSpan("Task.Execute.start-child");

        var executor = CreateExecutor(targetDomain: string.Empty);

        var result = await executor.RouteForTest(
            local: () => Task.FromResult("local"),
            remote: () => Task.FromResult("remote"));

        result.ShouldBe("local");
    }

    [Fact]
    public async Task SameDomain_ChildLaneRestartsTheOrdinal()
    {
        // The triggered instance is a different instance, so its hops number from zero rather than
        // continuing the caller's sequence.
        using var callerLane = WorkflowTraceLane.Use(
            "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-1111111111111111-01", parentAnchor: null, seq: 7);
        using var taskSpan = StartSpan("Task.Execute.start-child");

        var executor = CreateExecutor(LocalDomain);
        var seqInsideLocal = -1;

        await executor.RouteForTest(
            local: () =>
            {
                seqInsideLocal = WorkflowTraceLane.Seq;
                return Task.FromResult("local");
            },
            remote: () => Task.FromResult("remote"));

        seqInsideLocal.ShouldBe(0);
    }

    private static Activity StartSpan(string name)
    {
        var activity = new Activity(name);
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        return activity;
    }

    private static TestTriggerExecutor CreateExecutor(string targetDomain)
    {
        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.Domain.Returns(LocalDomain);

        return new TestTriggerExecutor(
            targetDomain,
            Substitute.For<IScriptEngine>(),
            runtimeInfo,
            Substitute.For<IRemoteInvokerService>(),
            Substitute.For<ILogger>());
    }

    /// <summary>
    /// Minimal subclass exposing <c>RouteAsync</c>. The routing decision and the lane policy live in
    /// the base class, so testing them there covers every executor in the family at once — and a new
    /// one cannot opt out, since <c>IsSameDomain</c> is private to the base.
    /// </summary>
    private sealed class TestTriggerExecutor(
        string targetDomain,
        IScriptEngine scriptEngine,
        IRuntimeInfoProvider runtimeInfoProvider,
        IRemoteInvokerService remoteInvoker,
        ILogger logger)
        : TriggerTaskExecutorBase<FakeTriggerTask>(scriptEngine, runtimeInfoProvider, remoteInvoker, logger)
    {
        public override TaskType TaskType => TaskType.StartTrigger;

        protected override string GetTargetDomain(FakeTriggerTask task) => targetDomain;

        // Fully qualified: BBT.Workflow.Execution also declares a TaskInvocationResult, and the
        // executor contract uses the BBT.Workflow.Tasks one.
        protected override System.Threading.Tasks.Task<
            BBT.Aether.Results.Result<BBT.Workflow.Tasks.TaskInvocationResult>> InvokeAsync(
            FakeTriggerTask task,
            TaskExecutorContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public System.Threading.Tasks.Task<string> RouteForTest(Func<Task<string>> local, Func<Task<string>> remote)
            => RouteAsync(CreateTask(), local, remote);

        private static FakeTriggerTask CreateTask()
        {
            var task = new FakeTriggerTask();
            task.SetReference(new Reference("start-child", LocalDomain, "sys-tasks", "1.0.0"));
            return task;
        }
    }

    /// <summary>
    /// Stand-in for the trigger task types. RouteAsync reads only the task's key (for the routing
    /// log) and its target domain (through GetTargetDomain), so a bare WorkflowTask carries
    /// everything the routing decision needs — and it keeps this test independent of any one
    /// concrete task's config schema.
    /// </summary>
    private sealed class FakeTriggerTask : WorkflowTask
    {
        public override WorkflowTask Clone() => new FakeTriggerTask();
    }
}
