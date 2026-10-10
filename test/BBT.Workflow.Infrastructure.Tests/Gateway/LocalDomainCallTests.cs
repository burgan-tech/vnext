using System.Threading.Tasks;
using BBT.Workflow.Gateway;
using BBT.Workflow.Runtime;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Gateway;

/// <summary>
/// Covers <see cref="LocalDomainCall"/>: a routed gateway's local branch for a co-hosted domain runs
/// as the target domain for its whole async run, and the caller's domain is back right after the call
/// is started.
/// </summary>
public sealed class LocalDomainCallTests
{
    [Fact]
    public async Task Run_TargetDomainHoldsAcrossAwait_AndCallerDomainIsRestored()
    {
        using (DomainScope.Begin("core"))
        {
            var gate = new TaskCompletionSource();

            async Task<string?> LocalBranchAsync()
            {
                await gate.Task;
                return DomainScope.Current;
            }

            var pending = LocalDomainCall.Run("partner", LocalBranchAsync);

            DomainScope.Current.ShouldBe("core");

            gate.SetResult();
            (await pending).ShouldBe("partner");
            DomainScope.Current.ShouldBe("core");
        }
    }

    [Fact]
    public void Run_BlankDomain_LeavesTheCurrentDomain()
    {
        using (DomainScope.Begin("core"))
        {
            LocalDomainCall.Run(null, () => DomainScope.Current).ShouldBe("core");
        }
    }
}
