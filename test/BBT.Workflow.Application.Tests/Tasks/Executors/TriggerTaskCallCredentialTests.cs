using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Resilience;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// The instance-call credential rule for the three trigger tasks that call another instance (StartTrigger, SubProcess,
/// DirectTrigger): a credential the task's header mapping SET — blank included — is the mapping's decision, only an ABSENT
/// credential is filled from the caller's request headers. In-process the blank is dropped and the call runs as the
/// resolved header set's user; cross-domain the blank stays in the binding as a marker for the remote invoker.
/// </summary>
public sealed class TriggerTaskCallCredentialTests
{
    private const string LocalDomain = "test-domain";
    private const string RemoteDomain = "other-domain";

    public enum Kind { Start, SubProcess, DirectTrigger }

    private static readonly Dictionary<string, string> Caller = new() { ["sub"] = "u1", ["act_sub"] = "a1" };

    private sealed record Observation(
        Dictionary<string, string?>? LocalHeaders,
        string? UserNameDuringCall,
        Dictionary<string, string?>? BindingHeaders);

    [Theory]
    [InlineData(Kind.Start)]
    [InlineData(Kind.SubProcess)]
    [InlineData(Kind.DirectTrigger)]
    public async Task Local_NoTaskHeaders_CarryTheCallerCredential(Kind kind)
    {
        var seen = await RunAsync(kind, remote: false, taskHeaders: null);

        seen.LocalHeaders.ShouldNotBeNull();
        seen.LocalHeaders!["sub"].ShouldBe("u1");
        seen.LocalHeaders["act_sub"].ShouldBe("a1");
    }

    [Theory]
    [InlineData(Kind.Start)]
    [InlineData(Kind.SubProcess)]
    [InlineData(Kind.DirectTrigger)]
    public async Task Local_BlankTaskCredential_IsNotSent(Kind kind)
    {
        var seen = await RunAsync(kind, remote: false, taskHeaders: """{ "sub": "" }""");

        seen.LocalHeaders.ShouldNotBeNull();
        seen.LocalHeaders!.Keys.ShouldNotContain("sub");
        seen.LocalHeaders["act_sub"].ShouldBe("a1");
    }

    [Theory]
    [InlineData(Kind.Start)]
    [InlineData(Kind.SubProcess)]
    [InlineData(Kind.DirectTrigger)]
    public async Task Local_RunsAsTheResolvedUser(Kind kind)
    {
        var currentUser = new BBT.Aether.Users.CurrentUser(AsyncLocalCurrentUserAccessor.Instance);
        using var ambient = currentUser.ChangeFromHeaders(
            new Dictionary<string, string?> { ["sub"] = "ambient", ["act_sub"] = "ambient-actor" });

        var seen = await RunAsync(kind, remote: false, taskHeaders: """{ "sub": "other" }""", currentUser);

        seen.UserNameDuringCall.ShouldBe("other");
        currentUser.UserName.ShouldBe("ambient"); // restored once the call is over
    }

    [Theory]
    [InlineData(Kind.Start)]
    [InlineData(Kind.SubProcess)]
    [InlineData(Kind.DirectTrigger)]
    public async Task Remote_BlankTaskCredential_StaysAsAMarkerInTheBinding(Kind kind)
    {
        var seen = await RunAsync(kind, remote: true, taskHeaders: """{ "sub": "" }""");

        seen.BindingHeaders.ShouldNotBeNull();
        seen.BindingHeaders!["sub"].ShouldBe(string.Empty);
        seen.BindingHeaders["act_sub"].ShouldBe("a1");
    }

    [Theory]
    [InlineData(Kind.Start)]
    [InlineData(Kind.SubProcess)]
    [InlineData(Kind.DirectTrigger)]
    public async Task Remote_NoTaskHeaders_CarryTheCallerCredential(Kind kind)
    {
        var seen = await RunAsync(kind, remote: true, taskHeaders: null);

        seen.BindingHeaders.ShouldNotBeNull();
        seen.BindingHeaders!["sub"].ShouldBe("u1");
        seen.BindingHeaders["act_sub"].ShouldBe("a1");
    }

    private static async Task<Observation> RunAsync(
        Kind kind, bool remote, string? taskHeaders, BBT.Aether.Users.CurrentUser? currentUser = null)
    {
        currentUser ??= new BBT.Aether.Users.CurrentUser(AsyncLocalCurrentUserAccessor.Instance);
        var domain = remote ? RemoteDomain : LocalDomain;
        var headersJson = taskHeaders is null ? string.Empty : $", \"headers\": {taskHeaders}";

        string? userDuringCall = null;
        Dictionary<string, string?>? localHeaders = null;
        TaskEnvelope? envelope = null;

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns(LocalDomain);

        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.StartAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                localHeaders = new Dictionary<string, string?>(call.Arg<StartInstanceInput>().Headers);
                userDuringCall = currentUser.UserName;
                return Result<StartInstanceOutput>.Ok(new StartInstanceOutput());
            });
        gateway.TransitionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                localHeaders = new Dictionary<string, string?>(call.Arg<TransitionInput>().Headers);
                userDuringCall = currentUser.UserName;
                return Result<TransitionOutput>.Ok(new TransitionOutput());
            });

        var endpointResolver = Substitute.For<IDomainDiscoveryResolver>();
        endpointResolver.GetEndpointAsync(Arg.Any<string>(), Arg.Any<EndpointKind>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new DiscoveryEndpoint(EndpointKind.Url, new Uri("https://other-domain.local"), null)));

        var remoteInvoker = Substitute.For<IRemoteInvokerService>();
        remoteInvoker.CreateTraceContext(Arg.Any<ScriptContext>()).Returns(new TaskTraceContext());
        remoteInvoker.InvokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
                Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                envelope = call.ArgAt<TaskEnvelope>(2);
                return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(data: null, statusCode: 200, taskType: "x"));
            });

        WorkflowTask task;
        Func<TaskExecutorContext, Task> execute;
        switch (kind)
        {
            case Kind.Start:
            {
                var t = StartTask.Create($$"""{ "domain": "{{domain}}", "flow": "order"{{headersJson}} }""".ToJsonElement());
                var executor = new StartTriggerTaskExecutor(Substitute.For<IScriptEngine>(), runtime, remoteInvoker,
                    gateway, endpointResolver, currentUser, NullLogger<StartTriggerTaskExecutor>.Instance);
                task = t;
                execute = c => executor.ExecuteAsync(c, CancellationToken.None);
                break;
            }
            case Kind.SubProcess:
            {
                var t = SubProcessTask.Create($$"""{ "key": "sub-proc", "domain": "{{domain}}", "flow": "doc", "version": "1.0.0"{{headersJson}} }""".ToJsonElement());
                var executor = new SubProcessTaskExecutor(Substitute.For<IScriptEngine>(), runtime, remoteInvoker,
                    gateway, Substitute.For<IInstanceRepository>(), Substitute.For<IGuidGenerator>(),
                    new ConfigurationBuilder().Build(), endpointResolver, currentUser,
                    NullLogger<SubProcessTaskExecutor>.Instance);
                task = t;
                execute = c => executor.ExecuteAsync(c, CancellationToken.None);
                break;
            }
            default:
            {
                var t = DirectTriggerTask.Create(
                    $$"""{ "domain": "{{domain}}", "flow": "order", "transitionName": "approve", "key": "{{Guid.NewGuid()}}"{{headersJson}} }""".ToJsonElement());
                var factory = Substitute.For<IResultResiliencePipelineFactory>();
                factory.CreatePipeline<TransitionOutput>(Arg.Any<string>())
                    .ReturnsForAnyArgs(ResiliencePipeline<Result<TransitionOutput>>.Empty);
                var executor = new DirectTriggerTaskExecutor(Substitute.For<IScriptEngine>(), runtime, remoteInvoker,
                    gateway, Substitute.For<IInstanceIdResolver>(), endpointResolver, factory, currentUser,
                    NullLogger<DirectTriggerTaskExecutor>.Instance);
                task = t;
                execute = c => executor.ExecuteAsync(c, CancellationToken.None);
                break;
            }
        }

        task.SetReference(new Reference("trigger-task", LocalDomain, "sys-tasks", "1.0.0"));
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(runtime)
            .SetHeaders(Caller)
            .SetWorkflow(WorkflowFactory.CreateDefault())
            .SetTransition(TransitionFactory.CreateDefault())
            .SetInstance(Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key"))
            .Build();
        await execute(new TaskExecutorContext(
            task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow));

        return new Observation(localHeaders, userDuringCall, ReadBindingHeaders(envelope));
    }

    /// <summary>The binding's <c>Headers</c> JSON string, parsed — whatever the binding type, so one reader serves all three.</summary>
    private static Dictionary<string, string?>? ReadBindingHeaders(TaskEnvelope? envelope)
    {
        if (envelope is null)
            return null;
        foreach (var property in envelope.Binding.EnumerateObject())
        {
            if (string.Equals(property.Name, "Headers", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return JsonSerializer.Deserialize<Dictionary<string, string?>>(property.Value.GetString()!);
        }

        return null;
    }
}
