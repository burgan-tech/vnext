using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Invocation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>
/// The function response cache is the hottest consumer of the state store: a hit skips the
/// function's whole task set, so paying a remote round trip (two, with a generation stamp) to
/// discover the hit defeats the cache. Routing it locally must not change WHAT it reports —
/// especially the CacheOk/Hit distinction, which is what keeps an outage from hiding behind a
/// plausible hit ratio.
/// </summary>
/// <remarks>
/// Builds a REAL <see cref="TaskInvocationDispatcher"/> over stubbed collaborators (mirrors
/// <c>TaskInvocationDispatcherTests.Harness</c>), so the gateway is proven to honour the routing
/// decision end to end rather than trusting a mocked dispatcher.
/// <para>
/// This namespace has both <c>BBT.Workflow.Execution</c> (for <see cref="TaskTypes"/>) and
/// <c>BBT.Workflow.Tasks</c> in scope; both declare identically-named
/// <c>TaskEnvelope</c>/<c>TaskInvocationResult</c>/<c>TaskTraceContext</c> twins, so every
/// unqualified reference below to one of those three is fully qualified with
/// <c>BBT.Workflow.Tasks.</c> to avoid CS0104 — the gateway works with the orchestrator-side
/// family, never the wire-side one.
/// </para>
/// </remarks>
public sealed class StateStoreCacheGatewayRoutingTests
{
    [Fact]
    public async Task GetAsync_LocalMode_ReadsThroughTheLocalInvokerAndReportsAHit()
    {
        var fixture = new CacheGatewayFixture(ExecutionMode.Local);
        fixture.SeedHit("""{"value":1}""");

        var result = await fixture.Gateway.GetAsync(
            "k1", storeName: null, consistency: null, fixture.TraceContext);

        result.CacheOk.ShouldBeTrue();
        result.Hit.ShouldBeTrue();
        fixture.RemoteInvocations.Count.ShouldBe(0);
        fixture.LocalInvocations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_LocalMode_StoreFailureIsReportedAsNotOkNotAsAMiss()
    {
        var fixture = new CacheGatewayFixture(ExecutionMode.Local);
        fixture.SeedFailure();

        var result = await fixture.Gateway.GetAsync(
            "k1", storeName: null, consistency: null, fixture.TraceContext);

        result.CacheOk.ShouldBeFalse();
        result.Hit.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_RemoteMode_KeepsUsingTheExecutionService()
    {
        var fixture = new CacheGatewayFixture(ExecutionMode.Remote);
        fixture.SeedHit("""{"value":1}""");

        var result = await fixture.Gateway.GetAsync(
            "k1", storeName: null, consistency: null, fixture.TraceContext);

        result.Hit.ShouldBeTrue();
        fixture.RemoteInvocations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SetAsync_LocalMode_WritesThroughTheLocalInvoker()
    {
        var fixture = new CacheGatewayFixture(ExecutionMode.Local);

        var written = await fixture.Gateway.SetAsync(
            "k1", new { value = 1 }, ttlInSeconds: 60, storeName: null,
            consistency: null, fixture.TraceContext);

        written.ShouldBeTrue();
        fixture.LocalInvocations.Count.ShouldBe(1);
    }

    /// <summary>
    /// Same skeleton as <c>TaskInvocationDispatcherTests.Harness</c>: a fixed-decision router
    /// substitute, a recording local invoker and a recording remote invoker, wired into a REAL
    /// <see cref="TaskInvocationDispatcher"/> and a REAL <see cref="StateStoreCacheGateway"/>.
    /// </summary>
    private sealed class CacheGatewayFixture
    {
        public IStateStoreCacheGateway Gateway { get; }

        public BBT.Workflow.Tasks.TaskTraceContext TraceContext { get; } = new();

        /// <summary>Bindings observed by the local state-store invoker.</summary>
        public List<JsonElement> LocalInvocations { get; } = [];

        /// <summary>Envelopes observed by the remote invoker (Execution service).</summary>
        public List<BBT.Workflow.Tasks.TaskEnvelope> RemoteInvocations { get; } = [];

        private BBT.Workflow.Tasks.TaskInvocationResult _seededResult = BBT.Workflow.Tasks.TaskInvocationResult.Success();

        public CacheGatewayFixture(ExecutionMode mode)
        {
            var router = Substitute.For<ITaskInvocationRouter>();
            router.Resolve(Arg.Any<WorkflowTask>(), TaskTypes.StateStore)
                .Returns(new TaskInvocationDecision(mode, "test"));

            var registry = Substitute.For<ILocalTaskInvokerRegistry>();
            registry.Get(TaskTypes.StateStore).Returns(new RecordingLocalInvoker(this));

            var remoteInvoker = new RecordingRemoteInvoker(this);

            var dispatcher = new TaskInvocationDispatcher(
                router, registry, remoteInvoker,
                Options.Create(new TaskInvocationOptions()), NullLogger<TaskInvocationDispatcher>.Instance);

            Gateway = new StateStoreCacheGateway(dispatcher);
        }

        /// <summary>Seeds a state-store hit: the next invocation (local or remote) returns <paramref name="json"/>.</summary>
        public void SeedHit(string json)
        {
            using var document = JsonDocument.Parse(json);
            _seededResult = BBT.Workflow.Tasks.TaskInvocationResult.Success(data: document.RootElement.Clone());
        }

        /// <summary>Seeds a transport failure — the shape a real Dapr error takes on the local path.</summary>
        public void SeedFailure()
        {
            _seededResult = BBT.Workflow.Tasks.TaskInvocationResult.Failure(
                error: "connection refused", taskType: TaskTypes.StateStore);
        }

        private sealed class RecordingLocalInvoker(CacheGatewayFixture owner) : ILocalTaskInvoker
        {
            public string TaskType => TaskTypes.StateStore;

            public Task<BBT.Workflow.Tasks.TaskInvocationResult> InvokeAsync(
                string? taskKey, JsonElement binding, BBT.Workflow.Tasks.TaskTraceContext? traceContext,
                CancellationToken cancellationToken = default)
            {
                owner.LocalInvocations.Add(binding.Clone());
                return Task.FromResult(owner._seededResult);
            }
        }

        private sealed class RecordingRemoteInvoker(CacheGatewayFixture owner) : IRemoteInvokerService
        {
            public Task<Result<BBT.Workflow.Tasks.TaskInvocationResult>> InvokeAsync(
                string taskType, string taskKey, BBT.Workflow.Tasks.TaskEnvelope envelope,
                BBT.Workflow.Tasks.TaskTraceContext traceContext,
                CancellationToken cancellationToken = default)
            {
                owner.RemoteInvocations.Add(envelope);
                return Task.FromResult(Result<BBT.Workflow.Tasks.TaskInvocationResult>.Ok(owner._seededResult));
            }

            public BBT.Workflow.Tasks.TaskTraceContext CreateTraceContext(ScriptContext scriptContext) => new();
        }
    }
}
