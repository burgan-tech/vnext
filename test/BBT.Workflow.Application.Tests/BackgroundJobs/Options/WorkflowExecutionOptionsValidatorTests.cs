using BBT.Workflow.BackgroundJobs.Options;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Shouldly;
using Xunit;

// NOTE: deliberately NOT "...Tests.BackgroundJobs.Options" — an "Options" segment here
// would shadow Microsoft.Extensions.Options for sibling test namespaces.
namespace BBT.Workflow.Application.Tests.DependencyInjection;

/// <summary>
/// Validation tests for <see cref="WorkflowExecutionOptionsValidator"/>:
/// the optimistic instance data reconciliation rollout flag requires the
/// latest-only aggregate loading prerequisite
/// (<see cref="WorkflowExecutionOptions.LatestOnlyInstanceLoading"/>).
/// </summary>
public sealed class WorkflowExecutionOptionsValidatorTests
{
    private static WorkflowExecutionOptionsValidator CreateValidator() =>
        new(Substitute.For<IConfiguration>());

    [Fact]
    public void Reconciliation_enabled_without_latest_only_loading_should_fail_validation()
    {
        var options = new WorkflowExecutionOptions
        {
            EnableInstanceDataReconciliation = true,
            LatestOnlyInstanceLoading = false
        };

        var result = CreateValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("LatestOnlyInstanceLoading");
    }

    [Fact]
    public void Reconciliation_enabled_with_latest_only_loading_should_pass_validation()
    {
        var options = new WorkflowExecutionOptions
        {
            EnableInstanceDataReconciliation = true,
            LatestOnlyInstanceLoading = true
        };

        CreateValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Reconciliation_disabled_should_pass_validation_without_latest_only_loading()
    {
        var options = new WorkflowExecutionOptions
        {
            EnableInstanceDataReconciliation = false,
            LatestOnlyInstanceLoading = false
        };

        CreateValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }
}
