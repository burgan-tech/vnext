using System.Collections.Generic;
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
    public void Validate_ZeroStatusLease_Fails()
    {
        // The status lock guards the Active→Busy admission check-and-set; a zero lease would
        // make every reserve fail.
        var options = new WorkflowExecutionOptions
        {
            StatusLockLeaseSeconds = 0
        };

        var result = CreateValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("StatusLockLeaseSeconds");
    }

    [Fact]
    public void Validate_PythonTimeoutMustFitInsideDaprInvocationTimeout()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Python:MaxTimeoutSeconds"] = "50",
                ["ExecutionApi:InvocationTimeoutSeconds"] = "50"
            })
            .Build();
        var validator = new WorkflowExecutionOptionsValidator(configuration);

        var result = validator.Validate(null, new WorkflowExecutionOptions());

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("must be smaller than ExecutionApi:InvocationTimeoutSeconds");
    }
}
