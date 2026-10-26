using System;
using System.Linq;
using System.Threading;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Execution;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Resilience;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks;

/// <summary>
/// DirectTrigger resolves an instance key through the local id-only port, never through the query gateway whose
/// GET applies row security (vnext#1107).
/// </summary>
public sealed class DirectTriggerKeyResolutionTests
{
    private const string Domain = "test-domain";
    private readonly IInstanceIdResolver _idResolver = Substitute.For<IInstanceIdResolver>();
    private readonly IInstanceQueryGateway _queryGateway = Substitute.For<IInstanceQueryGateway>();
    private readonly IInstanceCommandGateway _commandGateway = Substitute.For<IInstanceCommandGateway>();

    [Fact]
    public async System.Threading.Tasks.Task ByKey_UsesIdResolver_NotQueryGateway()
    {
        var id = Guid.NewGuid();
        _idResolver.ResolveIdAsync(Domain, "rs-a", "key-1", Arg.Any<CancellationToken>()).Returns(System.Threading.Tasks.Task.FromResult(Result<Guid>.Ok(id)));
        var result = await Execute(MakeTask("key-1"));

        result.IsSuccess.ShouldBeTrue();
        await _idResolver.Received(1).ResolveIdAsync(Domain, "rs-a", "key-1", Arg.Any<CancellationToken>());
        await _commandGateway.Received(1).TransitionAsync(id, "approve", Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>());
        await _queryGateway.DidNotReceiveWithAnyArgs().GetInstanceAsync(default!, default);
    }

    [Fact]
    public async System.Threading.Tasks.Task ById_SkipsLookup()
    {
        var id = Guid.NewGuid();
        await Execute(MakeTask(id.ToString()));

        await _idResolver.DidNotReceiveWithAnyArgs().ResolveIdAsync(default!, default!, default!, default);
        await _commandGateway.Received(1).TransitionAsync(id, "approve", Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async System.Threading.Tasks.Task UnknownKey_SameFailureAsBefore()
    {
        _idResolver.ResolveIdAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(Result<Guid>.Fail(Error.NotFound(WorkflowErrorCodes.InstanceNotFound, "nope")));
        var result = await Execute(MakeTask("missing"));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.StatusCode.ShouldBe(404);
        result.Value.ErrorMessage.ShouldBe("DirectTrigger instance could not be resolved to a valid identifier");
        await _commandGateway.DidNotReceiveWithAnyArgs().TransitionAsync(default, default!, default!, default);
    }

    private static DirectTriggerTask MakeTask(string key) => DirectTriggerTask.Create(
        JsonSerializer.Serialize(new { domain = Domain, flow = "rs-a", transitionName = "approve", key })
            .ToJsonElement());

    private async System.Threading.Tasks.Task<Result<BBT.Workflow.Scripting.StandardTaskResponse>> Execute(DirectTriggerTask task)
    {
        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns(Domain);
        var factory = Substitute.For<IResultResiliencePipelineFactory>();
        factory.CreatePipeline<TransitionOutput>(Arg.Any<string>())
            .ReturnsForAnyArgs(ResiliencePipeline<Result<TransitionOutput>>.Empty);
        _commandGateway.TransitionAsync(default, default!, default!, default)
            .ReturnsForAnyArgs(Result<TransitionOutput>.Ok(new TransitionOutput()));
        var executor = new DirectTriggerTaskExecutor(Substitute.For<IScriptEngine>(), runtime,
            Substitute.For<IRemoteInvokerService>(), _commandGateway, _idResolver,
            Substitute.For<IDomainDiscoveryResolver>(), factory, NullLogger<DirectTriggerTaskExecutor>.Instance);

        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(runtime)
            .SetInstance(Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key"))
            .Build();
        return await executor.ExecuteAsync(
            new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow),
            CancellationToken.None);
    }
}
