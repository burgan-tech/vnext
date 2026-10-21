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
    private static async Task<(InstanceCorrelation Correlation, IInstanceCorrelationRepository Repository)>
        RunAsync(string? resolvedVersion)
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
        var handler = new StartSubflowJobHandler(instances, correlations, starter, factory,
            Substitute.For<IRuntimeInfoProvider>(), Substitute.For<ILogger<StartSubflowJobHandler>>());

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
        return (correlation, correlations);
    }

    [Fact]
    public async Task Start_ShouldPinCorrelation_WhenChildReturnsFlowVersion()
    {
        var (correlation, repository) = await RunAsync("1.0.3");

        correlation.SubFlowVersion.ShouldBe("1.0.3");
        await repository.Received(1).TryPinSubFlowVersionAsync(
            correlation.Id, "1.0.3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_ShouldKeepAuthoredVersion_WhenChildReturnsNull()
    {
        var (correlation, repository) = await RunAsync(null);

        correlation.SubFlowVersion.ShouldBe("1.0");
        await repository.DidNotReceiveWithAnyArgs().TryPinSubFlowVersionAsync(default, default!, default);
    }
}
