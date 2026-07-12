using System.Linq;
using BBT.Aether.DependencyInjection;
using BBT.Workflow.DefinitionContext;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BBT.Workflow.Execution;

/// <summary>
/// Unit tests for the <see cref="TransitionExecutionContext.DeferInstanceEvents"/> gate on
/// <see cref="TransitionExecutionContext.ExtractAndDeferInstanceEvents"/>. In the historical
/// (defer) modes the events are pulled off the aggregate into the directives; in SinkDriven mode
/// (<c>DeferInstanceEvents = false</c>) they are left on the aggregate so the Aether domain-event
/// sink carries them.
/// </summary>
public class TransitionExecutionContextEventDeferTests : DomainTestBase<DomainEntryPoint>
{
    public TransitionExecutionContextEventDeferTests()
    {
        // InstanceFactory.CreateDefault seeds data via the [SchemaValidation]-woven AddData, which
        // resolves services from the ambient (AsyncLocal) provider — give the class a minimal one.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowContext>(new NullWorkflowContext());
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    private sealed class NullWorkflowContext : IWorkflowContext
    {
        public Definitions.Workflow? Workflow => null;
        public bool HasWorkflow => false;
        public void SetWorkflow(Definitions.Workflow workflow) { }
    }

    [Fact]
    public void Defer_Enabled_Extracts_Events_And_Clears_Aggregate()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.Fault("test-domain"); // raises a distributed event on the aggregate
        Assert.NotEmpty(instance.GetDomainEvents());

        var context = new TransitionExecutionContext { Instance = instance, DeferInstanceEvents = true };
        context.ExtractAndDeferInstanceEvents();

        Assert.True(context.Directives.HasDeferredEvents);
        Assert.Empty(instance.GetDomainEvents());
    }

    [Fact]
    public void Defer_Disabled_SinkDriven_Leaves_Events_On_Aggregate()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.Fault("test-domain");
        var before = instance.GetDomainEvents().Count;
        Assert.True(before > 0);

        var context = new TransitionExecutionContext { Instance = instance, DeferInstanceEvents = false };
        context.ExtractAndDeferInstanceEvents();

        // SinkDriven: nothing deferred, events remain on the aggregate for the sink to dispatch.
        Assert.False(context.Directives.HasDeferredEvents);
        Assert.Equal(before, instance.GetDomainEvents().Count);
    }
}
