using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

public sealed class ChildSubflowCancellationServiceTests
{
    [Fact]
    public async Task CancelChildSubflowAsync_ShouldUseDedicatedTypedGatewayOperation()
    {
        var instanceId = Guid.NewGuid();
        var termination = new TerminationContext(
            TerminationOrigin.ParentCascade,
            Guid.NewGuid(),
            Guid.NewGuid());
        var cancellationToken = new CancellationTokenSource().Token;
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.CancelChildAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ChildSubflowCancelInput>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        var sut = new ChildSubflowCancellationService(
            gateway,
            Substitute.For<ILogger<ChildSubflowCancellationService>>());

        await sut.CancelChildSubflowAsync(
            instanceId,
            "child-domain",
            "child-flow",
            "1.0.0",
            termination,
            cancellationToken);

        await gateway.Received(1).CancelChildAsync(
            instanceId,
            "child-domain",
            "child-flow",
            Arg.Is<ChildSubflowCancelInput>(input =>
                input.Version == "1.0.0" && input.Termination == termination),
            cancellationToken);
        await gateway.DidNotReceive().TransitionAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<Instances.TransitionInput>(),
            Arg.Any<CancellationToken>());
    }
}
