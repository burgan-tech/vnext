using System.Linq;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Execution.Transitions.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

// NOTE: deliberately NOT nested under a "...Tests.Microsoft.*" namespace — that would
// shadow the real Microsoft.* namespaces for every other test file in this assembly.
namespace BBT.Workflow.Application.Tests.DependencyInjection;

/// <summary>
/// Registration tests for <c>PipelineServiceCollectionExtensions.AddPipelineServices</c>:
/// the instance data reconciliation services must be resolvable by the production hosts.
/// </summary>
public sealed class PipelineServiceCollectionExtensionsTests
{
    [Fact]
    public void Pipeline_services_should_resolve_reconciliation_services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<WorkflowExecutionOptions>();
        services.AddPipelineServices();

        services.Any(x => x.ServiceType == typeof(IInstanceDataReconciliationService)).ShouldBeTrue();
        services.Any(x => x.ServiceType == typeof(IScriptDataChangeApplicator)).ShouldBeTrue();
    }
}
