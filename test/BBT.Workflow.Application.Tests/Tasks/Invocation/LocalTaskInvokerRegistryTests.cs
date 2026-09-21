using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Tasks.Invocation;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

public sealed class LocalTaskInvokerRegistryTests
{
    [Fact]
    public void Get_RegisteredType_ReturnsInvoker()
    {
        var registry = new LocalTaskInvokerRegistry([new FakeInvoker(TaskTypes.Http)]);

        registry.Get(TaskTypes.Http).ShouldNotBeNull();
        registry.Has(TaskTypes.Http).ShouldBeTrue();
    }

    [Fact]
    public void Get_UnregisteredType_ReturnsNull()
    {
        var registry = new LocalTaskInvokerRegistry([new FakeInvoker(TaskTypes.Http)]);

        registry.Get(TaskTypes.Soap).ShouldBeNull();
        registry.Has(TaskTypes.Soap).ShouldBeFalse();
    }

    [Fact]
    public void Get_TypeKeyIsCaseInsensitive()
    {
        var registry = new LocalTaskInvokerRegistry([new FakeInvoker(TaskTypes.Http)]);

        registry.Get("HTTP").ShouldNotBeNull();
    }

    private sealed class FakeInvoker(string taskType) : ILocalTaskInvoker
    {
        public string TaskType => taskType;

        // Fully qualified: BBT.Workflow.Tasks.TaskInvocationResult / TaskTraceContext are the
        // orchestrator-side twins ILocalTaskInvoker is built on. This test namespace also has
        // BBT.Workflow.Execution in scope (for TaskTypes), which declares identically-named
        // twins — an unqualified reference here is ambiguous (CS0104) even though production
        // code nested under BBT.Workflow.Tasks.* resolves it via enclosing-namespace lookup.
        public Task<BBT.Workflow.Tasks.TaskInvocationResult> InvokeAsync(
            string? taskKey, JsonElement binding, BBT.Workflow.Tasks.TaskTraceContext? traceContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BBT.Workflow.Tasks.TaskInvocationResult { IsSuccess = true });
    }
}
