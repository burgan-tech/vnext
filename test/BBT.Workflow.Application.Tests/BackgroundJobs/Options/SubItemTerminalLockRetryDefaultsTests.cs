using BBT.Workflow.BackgroundJobs.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.BackgroundJobs;

/// <summary>
/// Pins the per-sub-item terminal lock wait. The terminal RELAY sits on the client's synchronous
/// request and usually meets the child's creation-time <c>sub:state-changed</c>, which holds the same
/// key for one short transaction (4 ms p50 locally) — so the first retry, not the budget, is what the
/// client pays.
/// </summary>
public sealed class SubItemTerminalLockRetryDefaultsTests
{
    [Fact]
    public void First_retry_sleeps_at_most_ten_milliseconds()
    {
        var wait = new WorkflowExecutionOptions().SubItemTerminalLockRetry.ToLockAcquireWait();

        // Backoff is base * attempt + jitter(0..base): the first sleep is at most 2 * base.
        (wait.Delay.TotalMilliseconds * 2).ShouldBeLessThanOrEqualTo(10);
    }

    [Fact]
    public void Total_budget_stays_about_one_second()
    {
        var wait = new WorkflowExecutionOptions().SubItemTerminalLockRetry.ToLockAcquireWait();

        // Sleeps happen between attempts: sum over attempt 1..MaxAttempts-1 of base * attempt.
        var n = wait.MaxAttempts - 1;
        var minimumBudgetMs = wait.Delay.TotalMilliseconds * n * (n + 1) / 2;

        minimumBudgetMs.ShouldBeInRange(900, 1500);
    }
}
