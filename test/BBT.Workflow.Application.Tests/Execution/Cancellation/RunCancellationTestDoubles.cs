using System;
using System.Threading;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Execution.Cancellation;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace BBT.Workflow.Application.Tests.Execution.Cancellation;

/// <summary>Test doubles for services that take an <see cref="IRunCancellation"/>.</summary>
public static class RunCancellationTestDoubles
{
    /// <summary>
    /// A real <see cref="RunCancellation"/> on a host that is not stopping and the default 300 s budget,
    /// so a service under test runs on a live run token exactly as in production.
    /// </summary>
    public static IRunCancellation Live()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        return new RunCancellation(lifetime, MsOptions.Create(new WorkflowExecutionOptions()), TimeProvider.System);
    }
}
