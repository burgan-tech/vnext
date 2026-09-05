using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

public class SubflowStarterTests
{
    [Theory]
    [InlineData("S", ExecMode.Async)]
    [InlineData("S", ExecMode.Sync)]
    [InlineData("S", ExecMode.Resume)]
    [InlineData("P", ExecMode.Async)]
    [InlineData("P", ExecMode.Sync)]
    [InlineData("P", ExecMode.Resume)]
    public async Task StartAsync_ShouldAlwaysStartChildSynchronously(string type, ExecMode callerMode)
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        var starter = new SubflowStarter(gateway, new ConfigurationBuilder().Build(),
            Substitute.For<IScriptEngine>(), Substitute.For<ILogger<SubflowStarter>>());
        var workflow = WorkflowFactory.CreateDefault();
        var parent = Instance.Create(Guid.NewGuid(), workflow.Key, workflow.Version, "parent");
        var state = StateFactory.CreateDefault("child", StateType.SubFlow);
        state.SetSubFlow(type, new Reference("child-flow", "remote", "sys-flows", "1.0.0"), null!, null);
        var childId = Guid.NewGuid();
        var correlation = InstanceCorrelation.Create(Guid.NewGuid(), parent.Id, state.Key,
            childId, type, "remote", "child-flow", "1.0.0");
        gateway.StartSubAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Ok(new StartInstanceOutput
            {
                Id = childId,
                Status = InstanceStatus.Active
            }));

        var result = await starter.StartAsync(workflow, parent, state,
            TransitionFactory.CreateDefault(), correlation, null!, callerMode);

        result.IsSuccess.ShouldBeTrue();
        await gateway.Received(1).StartSubAsync(
            Arg.Is<StartInstanceInput>(input => input.Sync && input.StrictIdempotency &&
                input.Instance.Id == childId && input.Domain == "remote"),
            Arg.Any<CancellationToken>());
    }
}
