using System;
using System.Threading.Tasks;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Scripting.Factory;

/// <summary>
/// Katman 2 / Task 5 (B10c): <c>ScriptContextBuilder.BuildScriptTransitionRequest</c> must
/// hand <see cref="ScriptContext.CurrentTransition"/> a lazily-materializing
/// <see cref="ScriptTransitionRequest"/> — the persisted transition body/header are only parsed
/// into dynamic <c>ExpandoObject</c> graphs if a script actually reads
/// <c>CurrentTransition.Data</c>/<c>.Header</c>. The <c>_instanceTransition == null</c> path
/// (no transition record — initial creation, queries, scheduled/auto transitions) is unchanged:
/// <c>CurrentTransition</c> stays null.
/// </summary>
public class ScriptContextBuilderLazyCurrentTransitionTests
{
    private static ScriptContextBuilder CreateBuilder() =>
        new(
            Substitute.For<IComponentCacheStore>(),
            Substitute.For<IInstanceRepository>(),
            NullLogger<ScriptContext>.Instance,
            NullLogger<RelatedInstanceAccessor>.Instance);

    private static InstanceTransition CreateInstanceTransition() =>
        InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "submit",
            "start",
            TriggerType.Manual,
            new JsonData("""{"orderId":42}"""),
            new JsonData("""{"Authorization":"Bearer xyz"}"""));

    [Fact]
    public async Task BuildAsync_WithCurrentTransition_MaterializesCorrectDataAndHeader_OnAccess()
    {
        var builder = CreateBuilder().WithCurrentTransition(CreateInstanceTransition());

        var context = await builder.BuildAsync();

        context.CurrentTransition.ShouldNotBeNull();
        ((int)context.CurrentTransition!.Data!.orderId).ShouldBe(42);
        // Header keys are normalized to lowercase (unchanged contract).
        ((string)context.CurrentTransition!.Header!.authorization).ShouldBe("Bearer xyz");
    }

    [Fact]
    public async Task BuildAsync_WithoutCurrentTransition_LeavesItNull_Unchanged()
    {
        var builder = CreateBuilder();

        var context = await builder.BuildAsync();

        context.CurrentTransition.ShouldBeNull();
    }
}
