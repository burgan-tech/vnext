using System.Threading.Tasks;
using BBT.Workflow.Execution.Pipeline;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Transitions.Pipeline;

/// <summary>
/// Unit tests for <see cref="ChainLockRegistry"/> async-flow scoping semantics.
/// </summary>
public class ChainLockRegistryTests : DomainTestBase<DomainEntryPoint>
{
    [Fact]
    public void IsHeld_WithoutRegistration_ShouldReturnFalse()
    {
        ChainLockRegistry.IsHeld("vnext:bank:flow:none").ShouldBeFalse();
    }

    [Fact]
    public async Task Register_ShouldBeVisibleToNestedAsyncCalls()
    {
        const string key = "vnext:bank:parent-flow:instance-1";

        ChainLockRegistry.Register(key);

        (await NestedIsHeldAsync(key)).ShouldBeTrue();
    }

    [Fact]
    public async Task Register_InsideChildAsyncMethod_ShouldNotLeakToCaller()
    {
        const string key = "vnext:bank:parent-flow:instance-2";

        await RegisterInChildScopeAsync(key);

        ChainLockRegistry.IsHeld(key).ShouldBeFalse();
    }

    [Fact]
    public async Task Register_ShouldIsolateParallelExecutionFlows()
    {
        const string keyA = "vnext:bank:parent-flow:flow-a";
        const string keyB = "vnext:bank:parent-flow:flow-b";

        var flowA = Task.Run(async () =>
        {
            ChainLockRegistry.Register(keyA);
            await Task.Yield();
            return (SeesOwn: ChainLockRegistry.IsHeld(keyA), SeesOther: ChainLockRegistry.IsHeld(keyB));
        });
        var flowB = Task.Run(async () =>
        {
            ChainLockRegistry.Register(keyB);
            await Task.Yield();
            return (SeesOwn: ChainLockRegistry.IsHeld(keyB), SeesOther: ChainLockRegistry.IsHeld(keyA));
        });

        var results = await Task.WhenAll(flowA, flowB);

        results[0].SeesOwn.ShouldBeTrue();
        results[0].SeesOther.ShouldBeFalse();
        results[1].SeesOwn.ShouldBeTrue();
        results[1].SeesOther.ShouldBeFalse();
    }

    [Fact]
    public async Task Register_MultipleKeysInSameChain_ShouldAllBeHeld()
    {
        const string outerKey = "vnext:bank:parent-flow:outer";
        const string innerKey = "vnext:bank:child-flow:inner";

        ChainLockRegistry.Register(outerKey);
        ChainLockRegistry.Register(innerKey);

        (await NestedIsHeldAsync(outerKey)).ShouldBeTrue();
        (await NestedIsHeldAsync(innerKey)).ShouldBeTrue();
    }

    private static async Task<bool> NestedIsHeldAsync(string key)
    {
        await Task.Yield();
        return ChainLockRegistry.IsHeld(key);
    }

    private static async Task RegisterInChildScopeAsync(string key)
    {
        await Task.Yield();
        ChainLockRegistry.Register(key);
        ChainLockRegistry.IsHeld(key).ShouldBeTrue();
    }
}
