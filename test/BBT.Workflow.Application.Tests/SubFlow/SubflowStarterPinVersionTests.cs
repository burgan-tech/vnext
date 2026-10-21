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

/// <summary>
/// The starter hands the child's resolved flow version back to its caller (the post-commit
/// handler persists it on the correlation); an older remote runtime answers without one.
/// </summary>
public class SubflowStarterPinVersionTests
{
    private static async Task<Result<string?>> StartAsync(string? childFlowVersion)
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        var starter = new SubflowStarter(gateway, new ConfigurationBuilder().Build(),
            Substitute.For<IScriptEngine>(), Substitute.For<ILogger<SubflowStarter>>());
        var workflow = WorkflowFactory.CreateDefault();
        var parent = Instance.Create(Guid.NewGuid(), workflow.Key, workflow.Version, "parent");
        var state = StateFactory.CreateDefault("child", StateType.SubFlow);
        state.SetSubFlow("S", new Reference("child-flow", "remote", "sys-flows", "1.0"), null!, null);
        var childId = Guid.NewGuid();
        var correlation = InstanceCorrelation.Create(Guid.NewGuid(), parent.Id, state.Key,
            childId, "S", "remote", "child-flow", "1.0");
        gateway.StartSubAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Ok(new StartInstanceOutput
            {
                Id = childId,
                Status = InstanceStatus.Active,
                FlowVersion = childFlowVersion
            }));

        return await starter.StartAsync(workflow, parent, state,
            TransitionFactory.CreateDefault(), correlation, null!, ExecMode.Async);
    }

    [Fact]
    public async Task Start_ShouldReturnResolvedVersion_WhenChildReturnsFlowVersion()
    {
        var result = await StartAsync("1.0.3");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("1.0.3");
    }

    [Fact]
    public async Task Start_ShouldReturnNull_WhenChildReturnsNoFlowVersion()
    {
        var result = await StartAsync(null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }
}
