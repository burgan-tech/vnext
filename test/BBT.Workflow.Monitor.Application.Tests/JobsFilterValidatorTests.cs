using System;
using BBT.Workflow.Monitor.Jobs;
using BBT.Workflow.Monitor.Jobs.Filters;
using Xunit;

namespace BBT.Workflow.Monitor.Application.Tests;

public sealed class JobsFilterValidatorTests
{
    private static readonly DateTime Lower = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Upper = new(2026, 6, 27, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DomainWide_BothBounds_ReturnsValid()
    {
        var filter = new MonitorJobFilterInput { CreatedAtGte = Lower, CreatedAtLte = Upper };
        Assert.Equal(JobsFilterValidation.Valid, JobsFilterValidator.Validate(filter, isDomainWide: true));
    }

    [Fact]
    public void DomainWide_NullFilter_ReturnsCreatedAtRequired()
    {
        Assert.Equal(JobsFilterValidation.CreatedAtRequired, JobsFilterValidator.Validate(null, isDomainWide: true));
    }

    [Fact]
    public void DomainWide_MissingUpperBound_ReturnsCreatedAtRequired()
    {
        var filter = new MonitorJobFilterInput { CreatedAtGte = Lower };
        Assert.Equal(JobsFilterValidation.CreatedAtRequired, JobsFilterValidator.Validate(filter, isDomainWide: true));
    }

    [Fact]
    public void DomainWide_MissingLowerBound_ReturnsCreatedAtRequired()
    {
        var filter = new MonitorJobFilterInput { CreatedAtLte = Upper };
        Assert.Equal(JobsFilterValidation.CreatedAtRequired, JobsFilterValidator.Validate(filter, isDomainWide: true));
    }

    [Fact]
    public void WorkflowScoped_NoBounds_ReturnsValid()
    {
        var filter = new MonitorJobFilterInput();
        Assert.Equal(JobsFilterValidation.Valid, JobsFilterValidator.Validate(filter, isDomainWide: false));
    }

    [Fact]
    public void WorkflowScoped_NullFilter_ReturnsValid()
    {
        Assert.Equal(JobsFilterValidation.Valid, JobsFilterValidator.Validate(null, isDomainWide: false));
    }

    [Fact]
    public void WorkflowScoped_ExactlyOneBound_ReturnsCreatedAtRange()
    {
        var filter = new MonitorJobFilterInput { CreatedAtLte = Upper };
        Assert.Equal(JobsFilterValidation.CreatedAtRange, JobsFilterValidator.Validate(filter, isDomainWide: false));
    }

    [Fact]
    public void BothBounds_LowerGreaterThanUpper_ReturnsCreatedAtRange()
    {
        var filter = new MonitorJobFilterInput { CreatedAtGte = Upper, CreatedAtLte = Lower };
        Assert.Equal(JobsFilterValidation.CreatedAtRange, JobsFilterValidator.Validate(filter, isDomainWide: true));
    }

    [Fact]
    public void BothBounds_Equal_ReturnsValid()
    {
        var filter = new MonitorJobFilterInput { CreatedAtGte = Lower, CreatedAtLte = Lower };
        Assert.Equal(JobsFilterValidation.Valid, JobsFilterValidator.Validate(filter, isDomainWide: true));
    }
}
