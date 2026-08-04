using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow;

/// <summary>
/// Covers <see cref="ViewDefinitionJsonConverter"/> and <see cref="SchemaSelectionJsonConverter"/>.
/// The view converter is shared with <c>State.view</c> / <c>Transition.view</c>, so the bare-reference
/// branch added for function contract slots must not change how any other shape deserializes.
/// </summary>
public sealed class ContractDefinitionConverterTests
{
    private const string ViewRef = """{"key":"v1","domain":"d","flow":"sys-views","version":"1.0.0"}""";
    private const string SchemaRef = """{"key":"s1","domain":"d","flow":"sys-schemas","version":"1.0.0"}""";

    // ─── ViewDefinitionJsonConverter ────────────────────────────────────────────

    [Fact]
    public void View_BareReference_ProducesOneRuleLessEntry()
    {
        var definition = ReadView(ViewRef);

        definition.ShouldNotBeNull();
        definition.Views.Count.ShouldBe(1);
        definition.Views[0].Rule.ShouldBeNull();
        definition.Views[0].View.Key.ShouldBe("v1");
    }

    [Fact]
    public void View_LegacySingleViewObject_StillWorks()
    {
        var definition = ReadView($$"""{"view":{{ViewRef}},"loadData":true,"extensions":["e1"]}""");

        definition.ShouldNotBeNull();
        definition.Views.Count.ShouldBe(1);
        definition.Views[0].LoadData.ShouldBe(true);
        definition.Views[0].Extensions.ShouldBe(["e1"]);
    }

    [Fact]
    public void View_WrappedViewsArray_StillWorks()
    {
        var definition = ReadView($$"""{"views":[{"view":{{ViewRef}}}]}""");

        definition.ShouldNotBeNull();
        definition.Views.Count.ShouldBe(1);
    }

    [Fact]
    public void View_BareArray_StillWorks()
    {
        var definition = ReadView($$"""[{"view":{{ViewRef}}}]""");

        definition.ShouldNotBeNull();
        definition.Views.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"foo":1}""")]
    [InlineData("""{"views":[]}""")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("7")]
    [InlineData("true")]
    public void View_UnrecognizedShapes_StillReturnNull(string json)
    {
        ReadView(json).ShouldBeNull();
    }

    /// <summary>
    /// The bare-reference branch keys on a string <c>key</c>. An object that merely happens to carry a
    /// non-string <c>key</c> is not a reference and must not be treated as one.
    /// </summary>
    [Fact]
    public void View_ObjectWithNonStringKey_IsNotTreatedAsAReference()
    {
        ReadView("""{"key":42}""").ShouldBeNull();
    }

    [Fact]
    public void View_IsHonoredOnState_ForEveryShape()
    {
        var bare = ReadStateView("\"view\": " + ViewRef);
        var legacy = ReadStateView("\"view\": {\"view\":" + ViewRef + "}");
        var array = ReadStateView("\"views\": [{\"view\":" + ViewRef + "}]");

        bare!.Views[0].View.Key.ShouldBe("v1");
        legacy!.Views[0].View.Key.ShouldBe("v1");
        array!.Views[0].View.Key.ShouldBe("v1");
    }

    // ─── SchemaSelectionJsonConverter ───────────────────────────────────────────

    [Fact]
    public void Schema_BareReference_ProducesOneRuleLessEntry()
    {
        var selection = ReadSchema(SchemaRef);

        selection.ShouldNotBeNull();
        selection.Schemas.Count.ShouldBe(1);
        selection.Schemas[0].Rule.ShouldBeNull();
        selection.Schemas[0].Schema.Key.ShouldBe("s1");
    }

    [Fact]
    public void Schema_WrappedSingleSchemaObject_Works()
    {
        var selection = ReadSchema($$"""{"schema":{{SchemaRef}}}""");

        selection.ShouldNotBeNull();
        selection.Schemas.Count.ShouldBe(1);
    }

    [Fact]
    public void Schema_BareArrayAndWrappedArray_Agree()
    {
        var bare = ReadSchema($$"""[{"schema":{{SchemaRef}}}]""");
        var wrapped = ReadSchema($$"""{"schemas":[{"schema":{{SchemaRef}}}]}""");

        bare!.Schemas[0].Schema.Key.ShouldBe(wrapped!.Schemas[0].Schema.Key);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"foo":1}""")]
    [InlineData("""{"schemas":[]}""")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("7")]
    public void Schema_UnrecognizedShapes_ReturnNull(string json)
    {
        ReadSchema(json).ShouldBeNull();
    }

    [Fact]
    public void Schema_WritesTheCanonicalWrappedArrayForm()
    {
        var selection = ReadSchema(SchemaRef)!;

        var json = JsonSerializer.Serialize(selection, JsonSerializerConstants.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("schemas", out var schemas).ShouldBeTrue();
        schemas.GetArrayLength().ShouldBe(1);
    }

    private static ViewDefinition? ReadView(string json) =>
        JsonSerializer.Deserialize<ViewDefinition>(json, OptionsWith(new ViewDefinitionJsonConverter()));

    private static SchemaSelection? ReadSchema(string json) =>
        JsonSerializer.Deserialize<SchemaSelection>(json, OptionsWith(new SchemaSelectionJsonConverter()));

    /// <summary>Deserializes a state carrying the given view fragment and returns its effective view.</summary>
    private static ViewDefinition? ReadStateView(string viewFragment)
    {
        var json = $$"""
            {
                "key": "s",
                "stateType": 2,
                "versionStrategy": "Minor",
                "labels": [],
                {{viewFragment}}
            }
            """;

        return JsonSerializer.Deserialize<State>(json, JsonSerializerConstants.JsonOptions)!.View;
    }

    private static JsonSerializerOptions OptionsWith(System.Text.Json.Serialization.JsonConverter converter)
    {
        var options = new JsonSerializerOptions(JsonSerializerConstants.JsonOptions);
        options.Converters.Add(converter);
        return options;
    }
}
