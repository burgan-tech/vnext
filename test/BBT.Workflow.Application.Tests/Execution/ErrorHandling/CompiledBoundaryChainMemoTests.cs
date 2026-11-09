using System.Linq;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.ErrorHandling;

/// <summary>
/// <see cref="CompiledBoundaryChain.Compile"/> runs on every task execution; the per-level compile is
/// memoized per <see cref="ErrorBoundary"/> definition object. The memo must hand back a chain that
/// resolves exactly like a fresh compile, and a different definition object must never share an entry.
/// </summary>
public sealed class CompiledBoundaryChainMemoTests
{
    private static ErrorBoundary Boundary() => ErrorBoundary.WithRules(
        new ErrorHandlerRule { Action = ErrorAction.Ignore, ErrorCodes = ["*"], Priority = 999 },
        new ErrorHandlerRule { Action = ErrorAction.Retry, ErrorCodes = ["503"], Priority = 1, RetryPolicy = new RetryPolicy { MaxRetries = 2 } },
        new ErrorHandlerRule { Action = ErrorAction.Abort, ErrorTypes = ["TimeoutException"], Priority = 1 },
        new ErrorHandlerRule { Action = ErrorAction.Log, ErrorCodes = ["404"], Priority = 5 });

    [Fact]
    public void SameDefinition_ReturnsTheSameCompiledBoundary_PerLevel()
    {
        var task = Boundary();
        var state = Boundary();

        var first = CompiledBoundaryChain.Compile(task, state, null);
        var second = CompiledBoundaryChain.Compile(task, state, null);

        second.TaskBoundary.ShouldBeSameAs(first.TaskBoundary);
        second.StateBoundary.ShouldBeSameAs(first.StateBoundary);
        second.GlobalBoundary.ShouldBeNull();
        first.TaskBoundary!.Level.ShouldBe(ErrorBoundaryLevel.Task);
        first.StateBoundary!.Level.ShouldBe(ErrorBoundaryLevel.State);
    }

    [Fact]
    public void SameDefinition_AtDifferentLevels_KeepsItsLevel()
    {
        var shared = Boundary();

        var chain = CompiledBoundaryChain.Compile(shared, shared, shared);

        chain.TaskBoundary!.Level.ShouldBe(ErrorBoundaryLevel.Task);
        chain.StateBoundary!.Level.ShouldBe(ErrorBoundaryLevel.State);
        chain.GlobalBoundary!.Level.ShouldBe(ErrorBoundaryLevel.Global);
    }

    [Fact]
    public void MemoizedChain_SortsAndMatchesExactlyLikeAFreshCompile()
    {
        var boundary = Boundary();
        CompiledBoundaryChain.Compile(boundary, null, null); // warm the memo

        var memoized = CompiledBoundaryChain.Compile(boundary, null, null).TaskBoundary!;
        var fresh = CompiledBoundary.Compile(boundary, ErrorBoundaryLevel.Task)!;

        memoized.SortedRules.Select(r => r.DefinitionOrder)
            .ShouldBe(fresh.SortedRules.Select(r => r.DefinitionOrder));
        foreach (var (type, code, status) in new (string, string?, int?)[]
                 {
                     ("TimeoutException", null, null), ("X", "503", 503), ("X", "404", 404), ("X", "500", 500)
                 })
        {
            int? memoOrder = memoized.FindMatch(type, code, status)?.DefinitionOrder;
            int? freshOrder = fresh.FindMatch(type, code, status)?.DefinitionOrder;
            memoOrder.ShouldBe(freshOrder);
        }
    }

    [Fact]
    public void ADifferentDefinitionObject_IsCompiledOnItsOwn()
    {
        var original = Boundary();
        var republished = original with { };

        var a = CompiledBoundaryChain.Compile(original, null, null).TaskBoundary;
        var b = CompiledBoundaryChain.Compile(republished, null, null).TaskBoundary;

        b.ShouldNotBeSameAs(a);
        b!.Boundary.ShouldBeSameAs(republished);
    }

    [Fact]
    public void EmptyOrMissingBoundary_CompilesToNull()
    {
        var chain = CompiledBoundaryChain.Compile(ErrorBoundary.Empty, null, new ErrorBoundary());

        chain.HasAnyBoundary.ShouldBeFalse();
    }
}
