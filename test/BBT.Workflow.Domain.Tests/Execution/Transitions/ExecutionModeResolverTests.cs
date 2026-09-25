using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution;

/// <summary>
/// Unit tests for <see cref="ExecutionModeResolver"/> — the precedence that makes a definition beat the
/// caller's query parameter and the transition (inner) beat the flow (outer), for vnext#1003. Covers the
/// full matrix: no definition, flow-only, transition-only, both (agree and conflict), and the
/// override-detection used to surface the requested-vs-effective divergence on the trace.
/// </summary>
public sealed class ExecutionModeResolverTests
{
    // ── No definition anywhere → the caller's requested mode stands (pre-#1003 behaviour) ──

    [Theory]
    [InlineData(ExecMode.Sync)]
    [InlineData(ExecMode.Async)]
    public void NoDefinition_UsesCallerMode(ExecMode caller) =>
        ExecutionModeResolver.Resolve(null, null, caller).ShouldBe(caller);

    // ── Flow-only (outer) → flow wins over the caller, regardless of what the caller asked ──

    [Theory]
    [InlineData(ExecMode.Sync)]
    [InlineData(ExecMode.Async)]
    public void FlowOnly_Sync_OverridesCaller(ExecMode caller) =>
        ExecutionModeResolver.Resolve(null, ExecutionType.Sync, caller).ShouldBe(ExecMode.Sync);

    [Theory]
    [InlineData(ExecMode.Sync)]
    [InlineData(ExecMode.Async)]
    public void FlowOnly_Async_OverridesCaller(ExecMode caller) =>
        ExecutionModeResolver.Resolve(null, ExecutionType.Async, caller).ShouldBe(ExecMode.Async);

    // ── Transition-only (inner) → transition wins over the caller ──

    [Theory]
    [InlineData(ExecMode.Sync)]
    [InlineData(ExecMode.Async)]
    public void TransitionOnly_OverridesCaller(ExecMode caller)
    {
        ExecutionModeResolver.Resolve(ExecutionType.Sync, null, caller).ShouldBe(ExecMode.Sync);
        ExecutionModeResolver.Resolve(ExecutionType.Async, null, caller).ShouldBe(ExecMode.Async);
    }

    // ── Both defined → the transition (inner) wins over the flow (outer) ──

    [Fact]
    public void TransitionSync_FlowAsync_TransitionWins()
    {
        // The user's headline case: flow=ASYNC, transition=SYNC, caller asked ASYNC → runs SYNC.
        ExecutionModeResolver.Resolve(ExecutionType.Sync, ExecutionType.Async, ExecMode.Async)
            .ShouldBe(ExecMode.Sync);
    }

    [Fact]
    public void TransitionAsync_FlowSync_TransitionWins()
    {
        ExecutionModeResolver.Resolve(ExecutionType.Async, ExecutionType.Sync, ExecMode.Sync)
            .ShouldBe(ExecMode.Async);
    }

    [Fact]
    public void BothAgree_UsesThatMode()
    {
        ExecutionModeResolver.Resolve(ExecutionType.Sync, ExecutionType.Sync, ExecMode.Async)
            .ShouldBe(ExecMode.Sync);
        ExecutionModeResolver.Resolve(ExecutionType.Async, ExecutionType.Async, ExecMode.Sync)
            .ShouldBe(ExecMode.Async);
    }

    // ── Override detection (requested vs effective) — the trace signal ──

    [Fact]
    public void IsOverridden_TrueWhenDefinitionDiffersFromCaller()
    {
        // transition SYNC, caller ASYNC → overridden.
        ExecutionModeResolver.IsOverriddenByDefinition(ExecutionType.Sync, ExecutionType.Async, ExecMode.Async)
            .ShouldBeTrue();
    }

    [Fact]
    public void IsOverridden_FalseWhenNoDefinition()
    {
        ExecutionModeResolver.IsOverriddenByDefinition(null, null, ExecMode.Async).ShouldBeFalse();
    }

    [Fact]
    public void IsOverridden_FalseWhenDefinitionMatchesCaller()
    {
        // Definition present but it agrees with what the caller asked → not an override.
        ExecutionModeResolver.IsOverriddenByDefinition(null, ExecutionType.Async, ExecMode.Async)
            .ShouldBeFalse();
    }
}
