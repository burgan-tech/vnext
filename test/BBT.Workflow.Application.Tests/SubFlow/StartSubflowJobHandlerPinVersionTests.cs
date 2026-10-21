using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.PostCommit.Handlers;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// The post-commit start handler persists the child's resolved version on the correlation, with a
/// single-column write; an older remote runtime that reports none leaves the authored reference.
/// </summary>
public class StartSubflowJobHandlerPinVersionTests
{
    private sealed class CapturingLogger : ILogger<StartSubflowJobHandler>
    {
        public System.Collections.Generic.List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static async Task<(InstanceCorrelation Correlation, IInstanceCorrelationRepository Repository, CapturingLogger Logger)>
        RunAsync(string? resolvedVersion, Action<IInstanceCorrelationRepository>? configureRepository = null)
    {
        var workflow = WorkflowFactory.CreateDefault();
        var parent = Instance.Create(Guid.NewGuid(), workflow.Key, workflow.Version, "parent");
        var state = StateFactory.CreateDefault("child", StateType.SubFlow);
        state.SetSubFlow("S", new Reference("child-flow", "remote", "sys-flows", "1.0"), null!, null);
        workflow.AddState(state);
        var correlation = InstanceCorrelation.Create(Guid.NewGuid(), parent.Id, state.Key,
            Guid.NewGuid(), "S", "remote", "child-flow", "1.0");
        parent.AddCorrelation(correlation);

        var instances = Substitute.For<IInstanceRepository>();
        instances.FindForSubflowStartAsync(parent.Id, correlation.Id, Arg.Any<CancellationToken>())
            .Returns(parent);

        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(parent)
            .Build();
        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithWorkflow(Arg.Any<Definitions.Workflow?>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance?>()).Returns(builder);
        builder.WithTransition(Arg.Any<Transition?>()).Returns(builder);
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithBody(Arg.Any<object?>()).Returns(builder);
        builder.WithHeaders(Arg.Any<System.Collections.Generic.Dictionary<string, string?>?>()).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>()).Returns(scriptContext);
        var factory = Substitute.For<IScriptContextFactory>();
        factory.NewBuilder(instances).Returns(builder);

        var starter = Substitute.For<ISubflowStarter>();
        starter.StartAsync(Arg.Any<Definitions.Workflow>(), Arg.Any<Instance>(), Arg.Any<State>(),
                Arg.Any<Transition>(), Arg.Any<InstanceCorrelation>(), Arg.Any<ScriptContext>(),
                Arg.Any<ExecMode>(), Arg.Any<CancellationToken>())
            .Returns(Result<string?>.Ok(resolvedVersion));

        var correlations = Substitute.For<IInstanceCorrelationRepository>();
        configureRepository?.Invoke(correlations);
        var logger = new CapturingLogger();
        var handler = new StartSubflowJobHandler(instances, correlations, starter, factory,
            Substitute.For<IRuntimeInfoProvider>(), logger);

        var context = new TransitionExecutionContext
        {
            InstanceId = parent.Id,
            Domain = "core",
            WorkflowKey = workflow.Key,
            TransitionKey = "go",
            Workflow = workflow,
            Instance = parent,
            Transition = TransitionFactory.CreateDefault(),
            CallerMode = ExecMode.Async
        };

        var result = await handler.HandleAsync(
            new StartSubflowJob(correlation.Id, state.Key, PostCommitContinuationBehavior.ContinueParent),
            context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        return (correlation, correlations, logger);
    }

    [Fact]
    public async Task Start_ShouldPinCorrelation_WhenChildReturnsFlowVersion()
    {
        var (correlation, repository, _) = await RunAsync("1.0.3");

        correlation.SubFlowVersion.ShouldBe("1.0.3");
        await repository.Received(1).TryPinSubFlowVersionAsync(
            correlation.Id, "1.0.3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_ShouldKeepAuthoredVersion_WhenChildReturnsNull()
    {
        var (correlation, repository, _) = await RunAsync(null);

        correlation.SubFlowVersion.ShouldBe("1.0");
        await repository.DidNotReceiveWithAnyArgs().TryPinSubFlowVersionAsync(default, default!, default);
    }

    [Fact]
    public async Task Start_ShouldSucceed_WhenPinThrows()
    {
        var (_, _, logger) = await RunAsync("1.0.3", r => r
            .TryPinSubFlowVersionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("db down")));

        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("pin"));
    }

    [Fact]
    public async Task Start_ShouldSucceed_AndWarn_WhenPinUpdatesNoRow()
    {
        var (_, _, logger) = await RunAsync("1.0.3", r => r
            .TryPinSubFlowVersionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false));

        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("no row updated"));
    }

    [Fact]
    public async Task Start_ShouldPropagateCancellation_WhenPinCancelled()
    {
        await Should.ThrowAsync<OperationCanceledException>(() => RunAsync("1.0.3", r => r
            .TryPinSubFlowVersionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new OperationCanceledException())));
    }
}
