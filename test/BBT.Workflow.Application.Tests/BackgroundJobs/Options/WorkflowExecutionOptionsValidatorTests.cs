using BBT.Workflow.BackgroundJobs.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.BackgroundJobs;

/// <summary>
/// Unit tests for <see cref="WorkflowExecutionOptionsValidator"/> — budget hierarchy and the
/// Busy-as-mutex guard rails (chain reaper required, sane status-lock lease).
/// </summary>
public class WorkflowExecutionOptionsValidatorTests
{
    private static WorkflowExecutionOptionsValidator CreateValidator()
        => new(new ConfigurationBuilder().Build());

    [Fact]
    public void Validate_Defaults_Succeeds()
    {
        var result = CreateValidator().Validate(null, new WorkflowExecutionOptions());

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_BusyAsMutexWithReaperDisabled_Fails()
    {
        // With Busy as the mutex there is no lock lease to expire — the reaper is the only
        // recovery for a crash-stranded Busy instance, so this combination must fail fast.
        var options = new WorkflowExecutionOptions
        {
            UseBusyAsMutex = true,
            EnableChainReaper = false
        };

        var result = CreateValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("EnableChainReaper");
    }

    [Fact]
    public void Validate_BusyAsMutexWithReaperEnabled_Succeeds()
    {
        var options = new WorkflowExecutionOptions
        {
            UseBusyAsMutex = true,
            EnableChainReaper = true
        };

        CreateValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_BusyAsMutexWithZeroStatusLease_Fails()
    {
        var options = new WorkflowExecutionOptions
        {
            UseBusyAsMutex = true,
            StatusLockLeaseSeconds = 0
        };

        var result = CreateValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("StatusLockLeaseSeconds");
    }
}
