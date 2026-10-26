using System;
using System.Linq;
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

    private static readonly MethodInfo PostProcess =
        typeof(SubProcessTaskExecutor).GetMethod("PostProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("PostProcessAsync not found.");

    private static Task<InstanceCorrelation> CreateAsync(string? resolvedVersion) =>
        RunAsync(resolvedVersion, viaPostProcess: false);

    // The remote result's metadata is JSON-deserialized, so its values are JsonElement, not string.
    private static Task<InstanceCorrelation> CreateFromRemoteMetadataAsync(string? resolvedVersion) =>
        RunAsync(resolvedVersion, viaPostProcess: true);

    private static async Task<InstanceCorrelation> RunAsync(string? resolvedVersion, bool viaPostProcess)
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

        Task<Result> run;
        if (viaPostProcess)
        {
            var metadata = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["SubFlowInstanceId"] = Guid.NewGuid(),
                    ["CorrelationId"] = Guid.NewGuid(),
                    [SubProcessMetadataKeys.ResolvedFlowVersion] = resolvedVersion
                }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var invocation = BBT.Workflow.Tasks.TaskInvocationResult.Success(
                taskType: "SubProcess", metadata: metadata);
            run = (Task<Result>)PostProcess.Invoke(executor, [task, invocation, context, CancellationToken.None])!;
        }
        else
        {
            run = (Task<Result>)CreateCorrelation.Invoke(executor,
                [context, task, Guid.NewGuid(), Guid.NewGuid(), resolvedVersion, CancellationToken.None])!;
        }

        var result = await run;

        result.IsSuccess.ShouldBeTrue();
        return instance.ChildCorrelations.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Start_ShouldPinCorrelation_WhenChildReturnsFlowVersion()
    {
        (await CreateAsync("1.0.3")).SubFlowVersion.ShouldBe("1.0.3");
    }

    [Fact]
    public async Task RemoteResult_ShouldPinCorrelation_ToJsonDeserializedResolvedVersion()
    {
        (await CreateFromRemoteMetadataAsync("1.1.0")).SubFlowVersion.ShouldBe("1.1.0");
    }

    [Fact]
    public async Task RemoteResult_ShouldFallBackToTriggerVersion_WhenNoVersionReported()
    {
        (await CreateFromRemoteMetadataAsync(null)).SubFlowVersion.ShouldBe("1.0");
    }

    [Fact]
    public async Task Start_ShouldKeepAuthoredVersion_WhenChildReturnsNull()
    {
        (await CreateAsync(null)).SubFlowVersion.ShouldBe("1.0");
    }
}
