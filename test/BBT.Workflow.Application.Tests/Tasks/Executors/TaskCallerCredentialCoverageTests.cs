using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Invocation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Every outbound task type carries the request's credential (sub, act_sub, position, client_id, role) for each credential
/// header its mapping left absent or empty; a non-empty mapping value wins. Pinned here for the task types that run through
/// a header dictionary (Start, in-process), a binding the task does not author (DaprHttpEndpoint) and the SOAP envelope.
/// </summary>
public sealed class TaskCallerCredentialCoverageTests
{
    private static readonly Dictionary<string, string> Caller = new()
    {
        ["sub"] = "alice", ["act_sub"] = "alice-actor", ["position"] = "HQ", ["client_id"] = "web-client",
        ["role"] = "morph-idm.maker", ["x-device-id"] = "device-1"
    };

    private static void ShouldCarryTheCaller(IReadOnlyDictionary<string, string?> headers, string expectedRole = "morph-idm.maker")
    {
        headers["sub"].ShouldBe("alice");
        headers["act_sub"].ShouldBe("alice-actor");
        headers["position"].ShouldBe("HQ");
        headers["client_id"].ShouldBe("web-client");
        headers["role"].ShouldBe(expectedRole);
        headers.Keys.ShouldNotContain("x-device-id");
    }

    [Fact]
    public async Task ASameDomainStart_PassesTheCallersCredential_TheMappingsRoleWins()
    {
        var task = StartTask.Create("""
            { "domain": "test-domain", "flow": "order", "headers": { "role": "svc.starter", "position": "" } }
            """.ToJsonElement());
        task.SetReference(new Reference("start-order", "test-domain", "sys-tasks", "1.0.0"));
        StartInstanceInput? sent = null;
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.StartAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.Arg<StartInstanceInput>();
                return Result<StartInstanceOutput>.Ok(new StartInstanceOutput());
            });
        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns("test-domain");
        var executor = new StartTriggerTaskExecutor(Substitute.For<IScriptEngine>(), runtime,
            Substitute.For<IRemoteInvokerService>(), gateway, Substitute.For<IDomainDiscoveryResolver>(),
            NullLogger<StartTriggerTaskExecutor>.Instance);

        await executor.ExecuteAsync(Context(task), CancellationToken.None);

        sent.ShouldNotBeNull();
        ShouldCarryTheCaller(sent!.Headers!, expectedRole: "svc.starter"); // empty "position" filled, role kept
        sent.TrustedPayload.ShouldBeTrue(); // the task body is flow-authored, not a client payload
    }

    [Fact]
    public async Task ADaprHttpEndpointCall_CarriesTheCallersCredentialInItsBinding()
    {
        var task = DaprHttpEndpointTask.Create("""
            { "endpointName": "partner-endpoint", "path": "/things", "method": "GET" }
            """.ToJsonElement());
        task.SetReference(new Reference("call-endpoint", "test-domain", "sys-tasks", "1.0.0"));
        BBT.Workflow.Tasks.TaskEnvelope? sent = null;
        var remoteInvoker = Substitute.For<IRemoteInvokerService>();
        remoteInvoker.CreateTraceContext(Arg.Any<ScriptContext>()).Returns(new BBT.Workflow.Tasks.TaskTraceContext());
        remoteInvoker.InvokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<BBT.Workflow.Tasks.TaskEnvelope>(),
                Arg.Any<BBT.Workflow.Tasks.TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.ArgAt<BBT.Workflow.Tasks.TaskEnvelope>(2);
                return Result<BBT.Workflow.Tasks.TaskInvocationResult>.Ok(
                    BBT.Workflow.Tasks.TaskInvocationResult.Success(data: JsonSerializer.SerializeToElement(new { ok = true })));
            });
        var executor = new DaprHttpEndpointTaskExecutor(remoteInvoker, Substitute.For<IScriptEngine>(),
            NullLogger<DaprHttpEndpointTaskExecutor>.Instance);

        await executor.ExecuteAsync(Context(task), CancellationToken.None);

        var binding = sent!.Binding.Deserialize<BBT.Workflow.Execution.Bindings.DaprHttpEndpointBinding>()!;
        ShouldCarryTheCaller(JsonSerializer.Deserialize<Dictionary<string, string?>>(binding.Headers!)!);
    }

    [Fact]
    public async Task ASoapCall_CarriesTheCallersCredentialInItsBinding()
    {
        var task = WorkflowTaskFactory.CreateSoapTask("call-soap");
        BBT.Workflow.Tasks.TaskEnvelope? sent = null;
        var dispatcher = Substitute.For<ITaskInvocationDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<WorkflowTask>(), Arg.Any<string>(), Arg.Any<BBT.Workflow.Tasks.TaskEnvelope>(),
                Arg.Any<BBT.Workflow.Tasks.TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.ArgAt<BBT.Workflow.Tasks.TaskEnvelope>(2);
                return Result<BBT.Workflow.Tasks.TaskInvocationResult>.Ok(
                    BBT.Workflow.Tasks.TaskInvocationResult.Success(data: JsonSerializer.SerializeToElement(new { ok = true })));
            });
        var remoteInvoker = Substitute.For<IRemoteInvokerService>();
        remoteInvoker.CreateTraceContext(Arg.Any<ScriptContext>()).Returns(new BBT.Workflow.Tasks.TaskTraceContext());
        var executor = new SoapTaskExecutor(remoteInvoker, Substitute.For<IScriptEngine>(), dispatcher,
            NullLogger<SoapTaskExecutor>.Instance);

        await executor.ExecuteAsync(Context(task), CancellationToken.None);

        var headers = JsonSerializer.Deserialize<Dictionary<string, string?>>(
            sent!.Binding.GetProperty("Headers").GetString()!)!;
        ShouldCarryTheCaller(headers);
    }

    private static TaskExecutorContext Context(WorkflowTask task)
    {
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetHeaders(Caller)
            .SetInstance(Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key"))
            .Build();
        return new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);
    }
}
