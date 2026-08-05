using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>
/// The four function contract slots are polymorphic on the wire: a bare component reference, a bare
/// entry array, or a wrapped object. All three must deserialize to the same in-memory model, because
/// the runtime evaluates them through one code path regardless of how they were authored.
/// </summary>
public sealed class FunctionContractSerializationTests
{
    private const string Domain = "test-domain";
    private const string Version = "1.0.0";

    public static TheoryData<string, string> SchemaSlots => new()
    {
        { "inputSchema", "sys-schemas" },
        { "outputSchema", "sys-schemas" }
    };

    public static TheoryData<string, string> ViewSlots => new()
    {
        { "inputView", "sys-views" },
        { "outputView", "sys-views" }
    };

    [Theory]
    [MemberData(nameof(SchemaSlots))]
    public void SchemaSlot_BareReference_BecomesASingleRuleLessEntry(string slot, string flow)
    {
        var function = Deserialize($$""" "{{slot}}": {{Ref("s1", flow)}} """);

        var selection = SelectSchema(function, slot);
        selection.ShouldNotBeNull();
        selection.Schemas.Count.ShouldBe(1);
        selection.Schemas[0].Rule.ShouldBeNull();
        selection.Schemas[0].Schema.Key.ShouldBe("s1");
    }

    [Theory]
    [MemberData(nameof(SchemaSlots))]
    public void SchemaSlot_EntryArray_PreservesOrderAndRules(string slot, string flow)
    {
        var function = Deserialize($$"""
            "{{slot}}": [
                { "rule": {{Rule()}}, "schema": {{Ref("s1", flow)}} },
                { "schema": {{Ref("s2", flow)}} }
            ]
            """);

        var selection = SelectSchema(function, slot);
        selection.ShouldNotBeNull();
        selection.Schemas.Count.ShouldBe(2);
        selection.Schemas[0].Rule.ShouldNotBeNull();
        selection.Schemas[0].Schema.Key.ShouldBe("s1");
        selection.Schemas[1].Rule.ShouldBeNull();
        selection.Schemas[1].Schema.Key.ShouldBe("s2");
    }

    [Theory]
    [MemberData(nameof(SchemaSlots))]
    public void SchemaSlot_WrappedArray_MatchesTheBareArray(string slot, string flow)
    {
        var wrapped = Deserialize($$""" "{{slot}}": { "schemas": [ { "schema": {{Ref("s1", flow)}} } ] } """);
        var bare = Deserialize($$""" "{{slot}}": [ { "schema": {{Ref("s1", flow)}} } ] """);

        SelectSchema(wrapped, slot)!.Schemas[0].Schema.Key
            .ShouldBe(SelectSchema(bare, slot)!.Schemas[0].Schema.Key);
    }

    [Theory]
    [MemberData(nameof(ViewSlots))]
    public void ViewSlot_BareReference_BecomesASingleRuleLessEntry(string slot, string flow)
    {
        var function = Deserialize($$""" "{{slot}}": {{Ref("v1", flow)}} """);

        var definition = SelectView(function, slot);
        definition.ShouldNotBeNull();
        definition.Views.Count.ShouldBe(1);
        definition.Views[0].Rule.ShouldBeNull();
        definition.Views[0].View.Key.ShouldBe("v1");
    }

    [Theory]
    [MemberData(nameof(ViewSlots))]
    public void ViewSlot_EntryArray_CarriesRuleAndLoadData(string slot, string flow)
    {
        var function = Deserialize($$"""
            "{{slot}}": [
                { "rule": {{Rule()}}, "view": {{Ref("v1", flow)}}, "loadData": true },
                { "view": {{Ref("v2", flow)}} }
            ]
            """);

        var definition = SelectView(function, slot);
        definition.ShouldNotBeNull();
        definition.Views.Count.ShouldBe(2);
        definition.Views[0].Rule.ShouldNotBeNull();
        definition.Views[0].LoadData.ShouldBe(true);
        definition.Views[1].Rule.ShouldBeNull();
        definition.Views[1].LoadData.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(ViewSlots))]
    public void ViewSlot_WrappedArray_MatchesTheBareArray(string slot, string flow)
    {
        var wrapped = Deserialize($$""" "{{slot}}": { "views": [ { "view": {{Ref("v1", flow)}} } ] } """);
        var bare = Deserialize($$""" "{{slot}}": [ { "view": {{Ref("v1", flow)}} } ] """);

        SelectView(wrapped, slot)!.Views[0].View.Key
            .ShouldBe(SelectView(bare, slot)!.Views[0].View.Key);
    }

    [Fact]
    public void UndeclaredSlots_AreNull_AndReportNoContract()
    {
        var function = Deserialize(null);

        function.InputSchema.ShouldBeNull();
        function.OutputSchema.ShouldBeNull();
        function.InputView.ShouldBeNull();
        function.OutputView.ShouldBeNull();
        function.HasInputSchema.ShouldBeFalse();
        function.HasOutputSchema.ShouldBeFalse();
        function.HasInputView.ShouldBeFalse();
        function.HasOutputView.ShouldBeFalse();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"nope\"")]
    [InlineData("42")]
    public void UnusableSlotValues_DegradeToNoContract(string value)
    {
        var function = Deserialize($$""" "inputView": {{value}}, "inputSchema": {{value}} """);

        function.InputView.ShouldBeNull();
        function.InputSchema.ShouldBeNull();
    }

    [Fact]
    public void RoundTrip_WritesTheCanonicalWrappedArrayForm()
    {
        var function = Deserialize($$"""
            "inputView": {{Ref("v1", "sys-views")}},
            "inputSchema": {{Ref("s1", "sys-schemas")}}
            """);

        var json = JsonSerializer.Serialize(function, JsonSerializerConstants.JsonOptions);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("inputView").TryGetProperty("views", out var views).ShouldBeTrue();
        views.GetArrayLength().ShouldBe(1);
        doc.RootElement.GetProperty("inputSchema").TryGetProperty("schemas", out var schemas).ShouldBeTrue();
        schemas.GetArrayLength().ShouldBe(1);

        // and the canonical form deserializes back to the same model
        var reparsed = JsonSerializer.Deserialize<Function>(json, JsonSerializerConstants.JsonOptions)!;
        reparsed.InputView!.Views[0].View.Key.ShouldBe("v1");
        reparsed.InputSchema!.Schemas[0].Schema.Key.ShouldBe("s1");
    }

    private static SchemaSelection? SelectSchema(Function function, string slot) =>
        slot == "inputSchema" ? function.InputSchema : function.OutputSchema;

    private static ViewDefinition? SelectView(Function function, string slot) =>
        slot == "inputView" ? function.InputView : function.OutputView;

    private static string Ref(string key, string flow) =>
        $$"""{ "key": "{{key}}", "domain": "{{Domain}}", "flow": "{{flow}}", "version": "{{Version}}" }""";

    private static string Rule() =>
        """{ "location": "", "code": "true", "encoding": "NAT" }""";

    private static Function Deserialize(string? slots)
    {
        var task = $$"""
            "task": {
                "order": 1,
                "task": { "key": "t", "domain": "{{Domain}}", "flow": "sys-tasks", "version": "{{Version}}" },
                "mapping": { "location": "", "code": "", "encoding": "NAT" }
            }
            """;

        var json = string.IsNullOrWhiteSpace(slots)
            ? $$"""{ "scope": "D", {{task}} }"""
            : $$"""{ "scope": "D", {{task}}, {{slots}} }""";

        return JsonSerializer.Deserialize<Function>(json, JsonSerializerConstants.JsonOptions)!;
    }
}
