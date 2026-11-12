using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Tests for EfCoreInstanceTransitionRepository implementation using InfrastructureEntryPoint.
/// </summary>
[Xunit.Collection(InfrastructureEntryPointCollection.Name)]
public class EfCoreInstanceTransitionRepositoryTests : InstanceTransitionRepositoryTests<InfrastructureEntryPoint>
{
    /// <inheritdoc />
    protected override async Task ForgetTrackedEntitiesAsync()
    {
        var context = await GetRequiredService<IAetherDbContextProvider<WorkflowDbContext>>().GetDbContextAsync();
        context.ChangeTracker.Clear();
    }
}
