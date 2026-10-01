using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
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
/// A DaprService task carries its caller's credential (sub, act_sub, position, client_id, role) in the binding's headers
/// wherever its own mapping sets none — the same rule as HTTP and the Get* reads. The caller values are its request
/// headers, so a role a provider resolved (morph-idm) is never among them.
/// </summary>
public sealed class DaprServiceTaskCallerCredentialTests
{
    [Fact]
    public async Task TheDispatchedBinding_CarriesTheCallersCredential_TheMappingsValueWins()
    {
        var task = DaprServiceTask.Create("""
            {
              "appId": "partner-api",
              "methodName": "/api/v1/things",
              "httpVerb": "GET",
              "headers": { "role": "svc.reader" }
            }
            """.ToJsonElement());
        task.SetReference(new Reference("call-partner", "test-domain", "sys-tasks", "1.0.0"));

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
        var executor = new DaprServiceTaskExecutor(remoteInvoker, Substitute.For<IScriptEngine>(), dispatcher,
            NullLogger<DaprServiceTaskExecutor>.Instance);

        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetHeaders(new Dictionary<string, string>
            {
                ["sub"] = "alice", ["act_sub"] = "alice-actor", ["position"] = "HQ", ["client_id"] = "web-client",
                ["role"] = "morph-idm.maker"
            })
            .SetInstance(Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key"))
            .Build();
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        await executor.ExecuteAsync(
            new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow),
            CancellationToken.None);

        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(
            sent!.Binding.GetProperty("Headers").GetString()!)!;
        headers["role"].ShouldBe("svc.reader");
        headers["sub"].ShouldBe("alice");
        headers["act_sub"].ShouldBe("alice-actor");
        headers["position"].ShouldBe("HQ");
        headers["client_id"].ShouldBe("web-client");
    }
}
