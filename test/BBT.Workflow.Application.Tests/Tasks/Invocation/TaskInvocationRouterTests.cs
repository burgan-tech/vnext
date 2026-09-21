using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Invocation;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// Pins the resolution order: task override → per-type config → default → capability gate.
/// The capability gate is what makes a misconfiguration fall back to the remote path instead
/// of failing the task.
/// </summary>
public sealed class TaskInvocationRouterTests
{
    [Fact]
    public void Resolve_NoConfiguration_IsRemote()
    {
        var router = CreateRouter(new TaskInvocationOptions(), localTypes: [TaskTypes.Http]);

        var decision = router.Resolve(CreateHttpTask(), TaskTypes.Http);

        decision.Mode.ShouldBe(ExecutionMode.Remote);
        decision.Reason.ShouldBe("default");
    }

    [Fact]
    public void Resolve_PerTypeOverride_BeatsDefault()
    {
        var options = new TaskInvocationOptions
        {
            DefaultMode = ExecutionMode.Remote,
            Modes = new Dictionary<string, ExecutionMode> { [TaskTypes.Http] = ExecutionMode.Local }
        };
        var router = CreateRouter(options, localTypes: [TaskTypes.Http]);

        var decision = router.Resolve(CreateHttpTask(), TaskTypes.Http);

        decision.Mode.ShouldBe(ExecutionMode.Local);
        decision.Reason.ShouldBe("type-config");
    }

    [Fact]
    public void Resolve_DefaultLocal_AppliesToEveryTypeWithALocalInvoker()
    {
        var options = new TaskInvocationOptions { DefaultMode = ExecutionMode.Local };
        var router = CreateRouter(options, localTypes: [TaskTypes.Http]);

        router.Resolve(CreateHttpTask(), TaskTypes.Http).Mode.ShouldBe(ExecutionMode.Local);
    }

    [Fact]
    public void Resolve_LocalRequestedButNoLocalInvokerRegistered_FallsBackToRemote()
    {
        var options = new TaskInvocationOptions
        {
            DefaultMode = ExecutionMode.Local,
            Modes = new Dictionary<string, ExecutionMode> { [TaskTypes.Python] = ExecutionMode.Local }
        };
        var router = CreateRouter(options, localTypes: [TaskTypes.Http]);

        var decision = router.Resolve(CreateHttpTask(), TaskTypes.Python);

        decision.Mode.ShouldBe(ExecutionMode.Remote);
        decision.Reason.ShouldBe("no-local-invoker");
    }

    [Fact]
    public void Resolve_TypeKeyIsCaseInsensitive()
    {
        var options = new TaskInvocationOptions
        {
            Modes = new Dictionary<string, ExecutionMode>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["HTTP"] = ExecutionMode.Local
            }
        };
        var router = CreateRouter(options, localTypes: [TaskTypes.Http]);

        router.Resolve(CreateHttpTask(), TaskTypes.Http).Mode.ShouldBe(ExecutionMode.Local);
    }

    private static TaskInvocationRouter CreateRouter(
        TaskInvocationOptions options, IReadOnlyCollection<string> localTypes) =>
        new(Options.Create(options), new StubLocalRegistry(localTypes));

    private static HttpTask CreateHttpTask()
    {
        var config = JsonDocument.Parse("""{"url":"https://x.local","method":"GET"}""").RootElement;
        var task = HttpTask.Create(config);
        task.SetReference(new Reference("probe", "core", "sys-tasks", "1.0.0"));
        return task;
    }

    private sealed class StubLocalRegistry(IReadOnlyCollection<string> types) : ILocalTaskInvokerRegistry
    {
        public ILocalTaskInvoker? Get(string taskType) =>
            types.Contains(taskType) ? new StubInvoker(taskType) : null;

        public bool Has(string taskType) => types.Contains(taskType);

        private sealed class StubInvoker(string taskType) : ILocalTaskInvoker
        {
            public string TaskType => taskType;

            // Fully qualified: BBT.Workflow.Tasks.TaskInvocationResult / TaskTraceContext are the
            // orchestrator-side twins ILocalTaskInvoker is built on (same twin pattern as
            // ExternalHttpTaskInvoker). This test namespace also has BBT.Workflow.Execution in
            // scope (for TaskTypes), which declares identically-named twins — an unqualified
            // reference here is ambiguous (CS0104) even though production code nested under
            // BBT.Workflow.Tasks.* resolves it via enclosing-namespace lookup.
            public Task<BBT.Workflow.Tasks.TaskInvocationResult> InvokeAsync(
                string? taskKey, JsonElement binding, BBT.Workflow.Tasks.TaskTraceContext? traceContext,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new BBT.Workflow.Tasks.TaskInvocationResult { IsSuccess = true });
        }
    }
}
