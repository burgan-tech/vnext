using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Execution;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Pins the concurrency contract of <c>SubProcessTaskExecutor.CreateCorrelationAsync</c>: N fan-out
/// items launching subprocesses off the SAME parent must not read-modify-write that parent
/// aggregate concurrently. They share one schema-bound DbContext through the ambient UnitOfWork,
/// and a Npgsql connection cannot run two commands at once — an overlap surfaces in production as
/// "A second operation was started on this context instance", losing that item's correlation and
/// leaving its subprocess untracked forever.
/// </summary>
public sealed class SubProcessCorrelationConcurrencyTests
{
    [Fact]
    public async Task CreateCorrelation_ConcurrentWritesForSameInstance_AreSerialized()
    {
        var harness = new CorrelationHarness();

        await Task.WhenAll(
            harness.CreateCorrelationAsync(),
            harness.CreateCorrelationAsync(),
            harness.CreateCorrelationAsync());

        harness.Tracker.MaxConcurrent.ShouldBe(
            1,
            "correlation writes on the same parent instance must serialize on the shared per-instance gate");
    }

    [Fact]
    public async Task CreateCorrelation_ConcurrentWritesForSameInstance_AllSucceed()
    {
        var harness = new CorrelationHarness();

        var results = await Task.WhenAll(
            harness.CreateCorrelationAsync(),
            harness.CreateCorrelationAsync(),
            harness.CreateCorrelationAsync());

        foreach (var result in results)
        {
            result.IsSuccess.ShouldBeTrue(result.Error.Message);
        }

        harness.Instance.ChildCorrelations.Count.ShouldBe(3);
    }

    /// <summary>
    /// The gate is SHARED, not per-writer. A holder standing in for the instance-data write path
    /// (which takes the very same <see cref="InstanceWriteGate"/>) must block the correlation path
    /// for the same instance id. Two separate striped arrays would let these overlap — which is
    /// precisely the collision this fix removes.
    /// </summary>
    [Fact]
    public async Task CreateCorrelation_DoesNotOverlapAnotherHolderOfTheSharedGate()
    {
        var harness = new CorrelationHarness();

        using (await InstanceWriteGate.AcquireAsync(harness.Instance.Id))
        {
            var correlationWrite = harness.CreateCorrelationAsync();

            // Give the correlation write a generous window to barge in if it were on its own gate.
            var raced = await Task.WhenAny(correlationWrite, Task.Delay(300));
            raced.ShouldNotBe(
                (Task)correlationWrite,
                "the correlation write must be blocked while another writer holds the shared gate");

            harness.Tracker.EverEntered.ShouldBeFalse();

            // Releasing the gate lets it through.
        }

        await harness.DrainAsync();
        harness.Tracker.EverEntered.ShouldBeTrue();
        harness.Tracker.MaxConcurrent.ShouldBe(1);
    }

    /// <summary>
    /// Striping must not degenerate into a global lock: two instances on DIFFERENT stripes run
    /// their correlation writes concurrently.
    /// </summary>
    [Fact]
    public async Task CreateCorrelation_DifferentInstancesOnDifferentStripes_RunConcurrently()
    {
        var (firstId, secondId) = GuidsOnDifferentStripes();

        var firstInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Each body announces itself and then waits for the other. They can only both proceed if
        // they are genuinely inside their gates at the same time; a gate that serialized every
        // instance would deadlock here and the WhenAny below would take the timeout branch.
        var first = new CorrelationHarness(firstId, onEnter: async () =>
        {
            firstInside.TrySetResult();
            await secondInside.Task;
        });
        var second = new CorrelationHarness(secondId, onEnter: async () =>
        {
            secondInside.TrySetResult();
            await firstInside.Task;
        });

        var writes = Task.WhenAll(first.CreateCorrelationAsync(), second.CreateCorrelationAsync());

        var raced = await Task.WhenAny(writes, Task.Delay(TimeSpan.FromSeconds(5)));
        raced.ShouldBe((Task)writes, "different stripes must not serialize against each other");
    }

    private static (Guid First, Guid Second) GuidsOnDifferentStripes()
    {
        var first = Guid.NewGuid();
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidate = Guid.NewGuid();
            if (InstanceWriteGate.StripeIndexOf(candidate) != InstanceWriteGate.StripeIndexOf(first))
            {
                return (first, candidate);
            }
        }

        throw new InvalidOperationException("Could not find two Guids on different stripes.");
    }

    /// <summary>
    /// Counts how many callers are inside the repository read-modify-write at once. Without the
    /// gate this reaches the number of concurrent fan-out items; with it, it never exceeds 1.
    /// </summary>
    private sealed class ReentrancyTracker
    {
        private int _active;
        private int _max;
        private int _everEntered;

        public int MaxConcurrent => Volatile.Read(ref _max);

        public bool EverEntered => Volatile.Read(ref _everEntered) == 1;

        public void Enter()
        {
            Volatile.Write(ref _everEntered, 1);
            var current = Interlocked.Increment(ref _active);

            var observed = Volatile.Read(ref _max);
            while (current > observed)
            {
                var previous = Interlocked.CompareExchange(ref _max, current, observed);
                if (previous == observed)
                {
                    break;
                }

                observed = previous;
            }
        }

        public void Exit() => Interlocked.Decrement(ref _active);
    }

    /// <summary>
    /// Builds a real <see cref="SubProcessTaskExecutor"/> over a repository substitute that
    /// records overlapping read-modify-writes, and invokes the private correlation writer.
    /// </summary>
    private sealed class CorrelationHarness
    {
        private static readonly MethodInfo CreateCorrelation =
            typeof(SubProcessTaskExecutor).GetMethod(
                "CreateCorrelationAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CreateCorrelationAsync not found.");

        private readonly SubProcessTaskExecutor _executor;
        private readonly TaskExecutorContext _context;
        private readonly SubProcessTask _task;
        private readonly List<Task<Result>> _issued = [];

        public CorrelationHarness(Guid? instanceId = null, Func<Task>? onEnter = null)
        {
            Instance = Instance.Create(instanceId ?? Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
            Instance.ChangeState(
                State.Create("awaiting-documents", StateType.Intermediate, StateSubType.None, "Patch"));

            var repository = Substitute.For<IInstanceRepository>();
            repository
                .FindWithAllCorrelationsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(_ => EnterAsync(onEnter));
            repository
                .UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(_ => ExitAsync());

            var logger = Substitute.For<ILogger<SubProcessTaskExecutor>>();
            logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

            _executor = new SubProcessTaskExecutor(
                Substitute.For<IScriptEngine>(),
                Substitute.For<IRuntimeInfoProvider>(),
                Substitute.For<IRemoteInvokerService>(),
                Substitute.For<IInstanceCommandGateway>(),
                repository,
                Substitute.For<IGuidGenerator>(),
                new ConfigurationBuilder().Build(),
                Substitute.For<IDomainDiscoveryResolver>(),
                logger);

            _task = SubProcessTask.Create(JsonSerializer.SerializeToElement(new
            {
                key = "launch-doc-subprocess",
                domain = "core",
                flow = "document",
                version = "1.0.0"
            }));
            _task.SetReference(new Reference("launch-doc-subprocess", "core", "sys-tasks", "1.0.0"));

            var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
                .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
                .SetInstance(Instance)
                .Build();

            _context = new TaskExecutorContext(
                _task,
                OnExecuteTask.Create(1, _task, ScriptCode.FromNative(string.Empty)),
                scriptContext,
                null,
                TaskTrigger.OnExecute,
                TaskExecutionOrigin.Flow);
        }

        public Instance Instance { get; }

        public ReentrancyTracker Tracker { get; } = new();

        public Task<Result> CreateCorrelationAsync()
        {
            var invocation = (Task<Result>)CreateCorrelation.Invoke(
                _executor,
                [_context, _task, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None])!;

            _issued.Add(invocation);
            return invocation;
        }

        public Task DrainAsync() => Task.WhenAll(_issued);

        private async Task<Instance?> EnterAsync(Func<Task>? onEnter)
        {
            Tracker.Enter();

            if (onEnter is not null)
            {
                await onEnter();
            }
            else
            {
                // A real read hits the database; the delay is what makes an unguarded overlap
                // observable instead of accidentally interleaving too fast to catch.
                await Task.Delay(60);
            }

            return Instance;
        }

        private async Task<Instance> ExitAsync()
        {
            await Task.Delay(20);
            Tracker.Exit();
            return Instance;
        }
    }
}
