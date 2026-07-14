using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Filtering;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// Verifies the runtime-set <see cref="GetInstancesTask.FilterSpec"/> path: a task configured with
/// the fluent <see cref="InstanceQuery"/> builder must produce exactly the same
/// <see cref="GetInstancesBinding"/> wire strings as the equivalent hand-written GraphQL filter JSON
/// (groupBy/aggregations routed inside the filter request envelope), while the legacy string path
/// stays byte-identical when no spec is set.
/// </summary>
public sealed class TaskBindingMapperGetInstancesFilterSpecTests
{
    [Fact]
    public void CreateEnvelope_WithoutFilterSpec_KeepsLegacyStringMappingUnchanged()
    {
        var task = CreateTask("""
            {
              "domain": "test-domain",
              "flow": "order",
              "filter": "{\"attributes\":{\"scopeGroup\":{\"eq\":\"bireysel-3\"}}}",
              "sort": "-CreatedAt"
            }
            """);

        var binding = CreateBinding(task);

        binding.Filter.ShouldBe("""{"attributes":{"scopeGroup":{"eq":"bireysel-3"}}}""");
        // The legacy mapping never populated Sort — must stay that way when no spec is set.
        binding.Sort.ShouldBeNull();
    }

    [Fact]
    public void CreateEnvelope_FilterOnlySpec_MatchesEquivalentHandWrittenFilterString()
    {
        var specTask = CreateTask();
        specTask.SetFilterSpec(InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .Where("currentState", f => f.Eq("complete"))
            .OrderByDescending("createdAt")
            .Build());

        var stringTask = CreateTask();
        stringTask.SetFilter(
            """{"and":[{"attributes":{"scopeGroup":{"eq":"bireysel-3"}}},{"currentState":{"eq":"complete"}}]}""");

        var specBinding = CreateBinding(specTask);
        var stringBinding = CreateBinding(stringTask);

        specBinding.Filter.ShouldBe(stringBinding.Filter);
        specBinding.Sort.ShouldBe("""{"fields":[{"field":"createdAt","direction":"desc"}]}""");
    }

    [Fact]
    public void CreateEnvelope_GroupBySpec_RoutesGroupByInsideFilterEnvelope_SameAsHandWritten()
    {
        var specTask = CreateTask();
        specTask.SetFilterSpec(InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .GroupBy("attributes.limitKey")
            .Sum("attributes.amount")
            .Build());

        var stringTask = CreateTask();
        stringTask.SetFilter(
            """{"filter":{"attributes":{"scopeGroup":{"eq":"bireysel-3"}}},"groupBy":{"fields":["attributes.limitKey"],"aggregations":{"sum":"attributes.amount"}}}""");

        var specBinding = CreateBinding(specTask);
        var stringBinding = CreateBinding(stringTask);

        specBinding.Filter.ShouldBe(stringBinding.Filter);

        // Structural sanity: the single filter parameter carries the request envelope.
        var envelope = JsonNode.Parse(specBinding.Filter!)!;
        envelope["filter"]!["attributes"]!["scopeGroup"]!["eq"]!.GetValue<string>().ShouldBe("bireysel-3");
        envelope["groupBy"]!["fields"]!.AsArray().Single()!.GetValue<string>().ShouldBe("attributes.limitKey");
        envelope["groupBy"]!["aggregations"]!["sum"]!.GetValue<string>().ShouldBe("attributes.amount");
    }

    [Fact]
    public void CreateEnvelope_IncludesSpec_MatchesEquivalentHandWrittenFilterString()
    {
        // Advisor-scope shape from rezervation mappings: primary advisor OR jsonb
        // containment on the participants array.
        var specTask = CreateTask();
        specTask.SetFilterSpec(InstanceQuery.Create()
            .OrGroup(
                q => q.Where("attributes.advisorId", f => f.Eq("adv-1")),
                q => q.Where("attributes.videoCallParticipants", f => f.Includes(new { userId = "adv-1" })))
            .Build());

        var stringTask = CreateTask();
        stringTask.SetFilter(
            """{"or":[{"attributes":{"advisorId":{"eq":"adv-1"}}},{"attributes":{"videoCallParticipants":{"includes":{"userId":"adv-1"}}}}]}""");

        CreateBinding(specTask).Filter.ShouldBe(CreateBinding(stringTask).Filter);
    }

    private static readonly string[] Expected =
    [
        "attributes.scopeGroup", "attributes.user", "attributes.scope", "attributes.limitKey"
    ];

    [Fact]
    public void CreateEnvelope_GroupBySpec_BindingValuesParseThroughTheEndpointParsers()
    {
        var task = CreateTask();
        task.SetFilterSpec(InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .GroupBy("attributes.scopeGroup", "attributes.user", "attributes.scope", "attributes.limitKey")
            .Sum("attributes.amount")
            .OrderByDescending("createdAt")
            .Build());

        var binding = CreateBinding(task);

        // The list endpoint (InstanceQueryAppService) detects the request envelope inside the
        // filter value — the same detection handwritten envelope strings rely on…
        GraphQLFilterParser.TryParseRequest(binding.Filter, out var request).ShouldBeTrue();
        request!.GroupBy!.GetFields().ShouldBe(Expected);
        request.GroupBy.Aggregations!.Sum.ShouldBe("attributes.amount");

        // …and applies the sort value over the envelope's OrderBy.
        var orderBy = GraphQLFilterParser.ParseOrderBy(binding.Sort);
        orderBy.ShouldNotBeNull();
        orderBy!.GetEntries().ShouldBe([("createdAt", "desc")]);
    }

    [Fact]
    public void CreateEnvelope_StandaloneAggregationsSpec_RoutesAggregationsInsideFilterEnvelope()
    {
        var specTask = CreateTask();
        specTask.SetFilterSpec(InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .Sum("attributes.amount")
            .Build());

        var stringTask = CreateTask();
        stringTask.SetFilter(
            """{"filter":{"attributes":{"scopeGroup":{"eq":"bireysel-3"}}},"aggregations":{"sum":"attributes.amount"}}""");

        CreateBinding(specTask).Filter.ShouldBe(CreateBinding(stringTask).Filter);
    }

    [Fact]
    public void SetFilterSpec_MaterializesFilterAndSortStrings_SoLocalExecutionMatchesTheBinding()
    {
        var task = CreateTask();
        task.SetFilterSpec(InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .GroupBy("attributes.limitKey")
            .Sum("attributes.amount")
            .OrderBy("attributes.limitKey")
            .Build());

        var binding = CreateBinding(task);

        // The local (same-domain) executor path reads task.Filter/task.Sort directly — they must
        // carry exactly what the remote binding carries.
        task.Filter.ShouldBe(binding.Filter);
        task.Sort.ShouldBe(binding.Sort);
    }

    [Fact]
    public void SetFilter_AfterSetFilterSpec_ClearsSpecAndRevertsToStringPath()
    {
        var task = CreateTask();
        task.SetFilterSpec(InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .Build());

        task.SetFilter("""{"currentState":{"eq":"complete"}}""");

        task.FilterSpec.ShouldBeNull();
        var binding = CreateBinding(task);
        binding.Filter.ShouldBe("""{"currentState":{"eq":"complete"}}""");
        binding.Sort.ShouldBeNull();
    }

    [Fact]
    public void CloneAndCopy_CarryFilterSpec_AndResetClearsIt()
    {
        var task = CreateTask();
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .Build();
        task.SetFilterSpec(spec);

        var cloned = task.CloneTyped();
        cloned.FilterSpec.ShouldBeSameAs(spec);
        cloned.Filter.ShouldBe(task.Filter);

        var pooled = GetInstancesTask.CreateEmpty();
        pooled.CopyFromInternal(task);
        pooled.FilterSpec.ShouldBeSameAs(spec);
        pooled.Filter.ShouldBe(task.Filter);

        pooled.Reset();
        pooled.FilterSpec.ShouldBeNull();
        pooled.Filter.ShouldBeNull();
    }

    private static GetInstancesTask CreateTask(string? configJson = null)
    {
        var task = GetInstancesTask.Create((configJson ?? """
            {
              "domain": "test-domain",
              "flow": "order"
            }
            """).ToJsonElement());
        task.SetReference(new Reference("get-instances", "test-domain", "sys-tasks", "1.0.0"));
        return task;
    }

    private static GetInstancesBinding CreateBinding(GetInstancesTask task)
    {
        var envelope = TaskBindingMapper.CreateEnvelope(task);
        envelope.IsSuccess.ShouldBeTrue();
        return envelope.Value!.Binding.Deserialize<GetInstancesBinding>()!;
    }
}
