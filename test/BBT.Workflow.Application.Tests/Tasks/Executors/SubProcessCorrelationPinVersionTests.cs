using System;
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
/// A SubProcess correlation is created after the child started, so it takes the child's resolved
/// flow version directly; an older remote runtime reports none and the authored reference stays.
/// </summary>
public sealed class SubProcessCorrelationPinVersionTests
{
    private static readonly MethodInfo CreateCorrelation =
        typeof(SubProcessTaskExecutor).GetMethod("CreateCorrelationAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("CreateCorrelationAsync not found.");

    private static async Task<InstanceCorrelation> CreateAsync(string? resolvedVersion)
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
        instance.ChangeState(State.Create("awaiting", StateType.Intermediate, StateSubType.None, "Patch"));

        var repository = Substitute.For<IInstanceRepository>();
        repository.FindWithAllCorrelationsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(instance);
        var logger = Substitute.For<ILogger<SubProcessTaskExecutor>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var executor = new SubProcessTaskExecutor(
            Substitute.For<IScriptEngine>(), Substitute.For<IRuntimeInfoProvider>(),
            Substitute.For<IRemoteInvokerService>(), Substitute.For<IInstanceCommandGateway>(),
            repository, Substitute.For<IGuidGenerator>(), new ConfigurationBuilder().Build(),
            Substitute.For<IDomainDiscoveryResolver>(), logger);

        var task = SubProcessTask.Create(JsonSerializer.SerializeToElement(new
        {
            key = "launch-doc-subprocess", domain = "core", flow = "document", version = "1.0"
        }));
        task.SetReference(new Reference("launch-doc-subprocess", "core", "sys-tasks", "1.0.0"));
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>()).SetInstance(instance).Build();
        var context = new TaskExecutorContext(task,
            OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty)), scriptContext, null,
            TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);

        var result = await (Task<Result>)CreateCorrelation.Invoke(executor,
            [context, task, Guid.NewGuid(), Guid.NewGuid(), resolvedVersion, CancellationToken.None])!;

        result.IsSuccess.ShouldBeTrue();
        return instance.ChildCorrelations.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Start_ShouldPinCorrelation_WhenChildReturnsFlowVersion()
    {
        (await CreateAsync("1.0.3")).SubFlowVersion.ShouldBe("1.0.3");
    }

    [Fact]
    public async Task Start_ShouldKeepAuthoredVersion_WhenChildReturnsNull()
    {
        (await CreateAsync(null)).SubFlowVersion.ShouldBe("1.0");
    }
}
