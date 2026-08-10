using System;
using System.Threading.Tasks;
using BBT.Workflow.Data;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

public class EfCoreInstanceRepositoryTests : InstanceRepositoryTests<InfrastructureEntryPoint>
{
    private IInstanceRepository Repository => GetRequiredService<IInstanceRepository>();

    [Fact]
    public async Task GetResultAsync_WithoutDetails_ShouldNotLoadDataOrCorrelations()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0");
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"value\":1}"));
        instance.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            "waiting",
            Guid.NewGuid(),
            SubFlowType.SubProcess.Code,
            "core",
            "child-workflow",
            "1.0.0"));

        await Repository.InsertAsync(instance, true);

        var dbContext = GetRequiredService<WorkflowDbContext>();
        dbContext.ChangeTracker.Clear();

        var detailedResult = await Repository.GetResultAsync(instance.Id.ToString(), includeDetails: true);
        detailedResult.IsSuccess.ShouldBeTrue();
        detailedResult.Value!.DataList.Count.ShouldBe(1);
        detailedResult.Value.ChildCorrelations.Count.ShouldBe(1);

        dbContext.ChangeTracker.Clear();

        var slimResult = await Repository.GetResultAsync(instance.Id.ToString(), includeDetails: false);
        slimResult.IsSuccess.ShouldBeTrue();
        slimResult.Value!.DataList.ShouldBeEmpty();
        slimResult.Value.ChildCorrelations.ShouldBeEmpty();
        dbContext.Entry(slimResult.Value).State.ShouldBe(EntityState.Unchanged);
    }
}
