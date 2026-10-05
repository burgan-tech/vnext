using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// A same-domain GetInstance read runs as the header set its remote invoker would send cross-domain — the task's own
/// headers, plus the pipeline caller's sub / act_sub where the task did not set them. <c>ICurrentUser</c> is AsyncLocal
/// and role resolution reads it before the headers, so the executor replaces it for the read and restores it afterwards.
/// </summary>
public sealed class TriggerTaskReadCredentialTests
{
    private const string LocalDomain = "test-domain";
    private readonly BBT.Aether.Users.CurrentUser _currentUser = new(AsyncLocalCurrentUserAccessor.Instance);

    private sealed record Observed(string[]? Roles, string? UserName, string? ActorUserName, bool IsAuthenticated);

    private IDisposable AmbientAuditor() => _currentUser.Change(new BasicUserInfo(
        "u-1", "alice", "Alice", "Doe", ["morph-idm.auditor"], "a-1", "alice-actor", null, "HQ"));

    [Fact]
    public async Task ALocalRead_RunsAsTheTaskHeaders_NotTheAmbientCaller()
    {
        var task = WorkflowTaskFactory.CreateGetInstanceTask(domain: LocalDomain);
        task.SetHeaders(new Dictionary<string, string?> { ["role"] = "morph-idm.maker", ["sub"] = "svc" });
        var (executor, observed) = Create();
        var caller = new Dictionary<string, string> { ["sub"] = "alice", ["act_sub"] = "alice-actor", ["role"] = "morph-idm.auditor" };

        using (AmbientAuditor())
        {
            (await executor.ExecuteAsync(Context(task, caller), CancellationToken.None)).IsSuccess.ShouldBeTrue();
            _currentUser.Roles.ShouldBe(["morph-idm.auditor"]); // restored after the read
        }

        observed().ShouldNotBeNull();
        observed()!.Roles.ShouldBe(["morph-idm.maker"]);       // the task's role wins over the caller's
        observed()!.UserName.ShouldBe("svc");                // the task's value wins
        observed()!.ActorUserName.ShouldBe("alice-actor");    // filled from the caller, as the wire would
    }

    [Fact]
    public async Task AHeaderlessTaskWithNoCallerIdentity_ReadsAsAnUnauthenticatedRoleLessCaller()
    {
        var task = WorkflowTaskFactory.CreateGetInstanceTask(domain: LocalDomain);
        var (executor, observed) = Create();

        using (AmbientAuditor())
            await executor.ExecuteAsync(Context(task), CancellationToken.None);

        observed()!.IsAuthenticated.ShouldBeFalse();
        (observed()!.Roles ?? []).ShouldBeEmpty();
    }

    /// <summary>
    /// A caller that sent no role header forwards none: the task read carries the caller's identity (so a provider such as
    /// morph-idm resolves roles for it at the target) but no role resolved for the caller is ever carried.
    /// </summary>
    [Fact]
    public async Task ACallerWithoutARoleHeader_ForwardsItsIdentityButNoRole()
    {
        var task = WorkflowTaskFactory.CreateGetInstanceTask(domain: LocalDomain);
        var (executor, observed) = Create();
        var caller = new Dictionary<string, string> { ["sub"] = "alice", ["act_sub"] = "alice-actor", ["position"] = "HQ" };

        using (AmbientAuditor())
            await executor.ExecuteAsync(Context(task, caller), CancellationToken.None);

        (observed()!.Roles ?? []).ShouldBeEmpty();
        observed()!.UserName.ShouldBe("alice");
        observed()!.ActorUserName.ShouldBe("alice-actor");
    }

    /// <summary>
    /// Same task, same caller, other domain: the remote binding carries exactly the header set the same-domain read runs as,
    /// so the target domain evaluates the same caller.
    /// </summary>
    [Fact]
    public async Task ACrossDomainRead_CarriesTheSameCredentialInItsBinding()
    {
        var task = WorkflowTaskFactory.CreateGetInstanceTask(domain: "other-domain");
        task.SetHeaders(new Dictionary<string, string?> { ["role"] = "svc.reader" });
        var caller = new Dictionary<string, string>
        {
            ["sub"] = "alice", ["act_sub"] = "alice-actor", ["position"] = "HQ", ["client_id"] = "web-client",
            ["role"] = "morph-idm.auditor", ["x-device-id"] = "device-1"
        };
        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns(LocalDomain);
        var endpointResolver = Substitute.For<IDomainDiscoveryResolver>();
        endpointResolver.GetEndpointAsync(Arg.Any<string>(), Arg.Any<EndpointKind>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new DiscoveryEndpoint(EndpointKind.Url, new Uri("https://other-domain.local"), null)));
        TaskEnvelope? sent = null;
        var remoteInvoker = Substitute.For<IRemoteInvokerService>();
        remoteInvoker.InvokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
                Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.ArgAt<TaskEnvelope>(2);
                return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(data: null, statusCode: 200, taskType: "18"));
            });
        var executor = new GetInstanceTaskExecutor(Substitute.For<IScriptEngine>(), runtime, remoteInvoker,
            Substitute.For<IInstanceQueryGateway>(), endpointResolver, _currentUser, NullLogger<GetInstanceTaskExecutor>.Instance);

        await executor.ExecuteAsync(Context(task, caller), CancellationToken.None);

        var binding = sent!.Binding.Deserialize<BBT.Workflow.Execution.Bindings.GetInstanceBinding>()!;
        var headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(binding.Headers!)!;
        headers["role"].ShouldBe("svc.reader");          // the task's own role wins
        headers["sub"].ShouldBe("alice");
        headers["act_sub"].ShouldBe("alice-actor");
        headers["position"].ShouldBe("HQ");
        headers["client_id"].ShouldBe("web-client");
        headers.Keys.ShouldNotContain("x-device-id");    // not a credential
    }

    private (GetInstanceTaskExecutor Executor, Func<Observed?> Observed) Create()
    {
        Observed? observed = null;
        var gateway = Substitute.For<IInstanceQueryGateway>();
        gateway.GetInstanceAsync(Arg.Any<GetInstanceInput>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            observed = new Observed(_currentUser.Roles, _currentUser.UserName, _currentUser.ActorUserName,
                _currentUser.IsAuthenticated);
            return ConditionalResult<GetInstanceOutput>.Success(new GetInstanceOutput());
        });
        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns(LocalDomain);
        var remoteInvoker = Substitute.For<IRemoteInvokerService>();

        var executor = new GetInstanceTaskExecutor(Substitute.For<IScriptEngine>(), runtime, remoteInvoker, gateway,
            Substitute.For<IDomainDiscoveryResolver>(), _currentUser, NullLogger<GetInstanceTaskExecutor>.Instance);
        return (executor, () => observed);
    }

    private static TaskExecutorContext Context(GetInstanceTask task, Dictionary<string, string>? callerHeaders = null)
    {
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetHeaders(callerHeaders)
            .SetInstance(Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key"))
            .Build();
        return new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);
    }
}
