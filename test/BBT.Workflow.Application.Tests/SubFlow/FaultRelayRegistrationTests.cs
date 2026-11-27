using System.Linq;
using BBT.Workflow.BackgroundJobs.Recovery;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.PostCommit.Relay;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// The three places that commit a direct fault take <see cref="IPostCommitRelayDispatcher"/> as an
/// optional parameter, so a composition root that forgot to register it would silently stop telling
/// a parent that its sub item faulted — the stuck-Busy parent of vnext#1256, with nothing failing.
/// These tests pin the registrations made by <c>AddApplicationModule</c>.
/// </summary>
public sealed class FaultRelayRegistrationTests
{
    [Fact]
    public void AddApplicationModule_Registers_The_Dispatcher_And_The_SubFaulted_Relay()
    {
        var services = new ServiceCollection();
        services.AddApplicationModule();

        services.Single(d => d.ServiceType == typeof(IPostCommitRelayDispatcher))
            .ImplementationType.ShouldBe(typeof(PostCommitRelayDispatcher));
        services.Single(d => d.ServiceType == typeof(IPostCommitEventRelay<InstanceSubFaultedEvent>))
            .ImplementationType.ShouldBe(typeof(InstanceSubFaultedRelay));
    }

    [Fact]
    public void The_Fault_Writers_Declare_The_Dispatcher_Parameter_Of_The_Registered_Type()
    {
        foreach (var faultWriter in new[]
                 {
                     typeof(TransitionPipeline),
                     typeof(PostCommitParentMutationService),
                     typeof(JobTimeoutRecoveryService)
                 })
        {
            faultWriter.GetConstructors().Single().GetParameters()
                .ShouldContain(p => p.ParameterType == typeof(IPostCommitRelayDispatcher));
        }
    }

    /// <summary>
    /// Without the correlation repository the start compensation never steps aside for a child that
    /// already settled its own fault (fail-safe, but the vnext#1256 ownership rule is then lost).
    /// </summary>
    [Fact]
    public void The_Post_Commit_Fault_Declares_The_Correlation_Repository_It_Needs_For_The_Guard()
    {
        typeof(PostCommitParentMutationService).GetConstructors().Single().GetParameters()
            .ShouldContain(p => p.ParameterType == typeof(BBT.Workflow.Instances.IInstanceCorrelationRepository));
    }

    [Fact]
    public void The_Fault_Writers_Are_Registered_By_AddApplicationModule()
    {
        var services = new ServiceCollection();
        services.AddApplicationModule();

        services.ShouldContain(d => d.ServiceType == typeof(TransitionPipeline));
        services.ShouldContain(d => d.ImplementationType == typeof(PostCommitParentMutationService));
        services.ShouldContain(d => d.ImplementationType == typeof(JobTimeoutRecoveryService));
    }
}
