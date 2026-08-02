using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions.Views;

/// <summary>
/// Unit tests for <see cref="ViewDisplayJsonConverter"/> and the <see cref="View"/> display accessors.
/// Covers the backward-compatible string form (SDI-only) alongside the { sdi, mdi } object form.
/// </summary>
public class ViewDisplayJsonConverterTests
{
    /// <summary>
    /// Options carrying the converter directly, used to exercise it in isolation. Serializing a whole
    /// <see cref="View"/> is not usable here because <c>View.SemanticVersion</c> requires a version,
    /// which component JSON only carries at the envelope level.
    /// </summary>
    private static readonly JsonSerializerOptions ConverterOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new ViewDisplayJsonConverter() }
    };

    private static View DeserializeView(string displayJson)
    {
        var json = $$"""{"type": 1, "content": "{}", "display": {{displayJson}}}""";
        var view = JsonSerializer.Deserialize<View>(json, JsonSerializerConstants.JsonOptions);
        view.ShouldNotBeNull();
        return view;
    }

    private static JsonElement SerializeDisplay(ViewDisplay? display)
    {
        var json = JsonSerializer.Serialize(display, ConverterOptions);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    // ── Reading: legacy string form ──────────────────────────────────────────

    [Fact]
    public void Read_LegacyStringForm_MapsToSdi()
    {
        var view = DeserializeView("\"popup\"");

        view.DisplayModes.ShouldNotBeNull();
        view.DisplayModes.Sdi.ShouldBe("popup");
        view.DisplayModes.Mdi.ShouldBeNull();
    }

    [Fact]
    public void Read_LegacyStringForm_LegacyDisplayAccessorReturnsSameValue()
    {
        var view = DeserializeView($"\"{ViewDisplayMode.Sdi.FullPage}\"");

        view.Display.ShouldBe(ViewDisplayMode.Sdi.FullPage);
        view.MdiDisplay.ShouldBeNull();
    }

    // ── Reading: object form ─────────────────────────────────────────────────

    [Fact]
    public void Read_ObjectForm_WithBothModes_MapsBoth()
    {
        var view = DeserializeView("""{"sdi": "popup", "mdi": "tab"}""");

        view.Display.ShouldBe("popup");
        view.MdiDisplay.ShouldBe("tab");
        view.DisplayModes!.Sdi.ShouldBe("popup");
        view.DisplayModes.Mdi.ShouldBe("tab");
    }

    [Fact]
    public void Read_ObjectForm_MdiOnly_LeavesLegacyDisplayEmpty()
    {
        var view = DeserializeView("""{"mdi": "window"}""");

        view.Display.ShouldBe(string.Empty);
        view.MdiDisplay.ShouldBe("window");
        view.DisplayModes!.Sdi.ShouldBeNull();
    }

    [Fact]
    public void Read_ObjectForm_SdiOnly_BehavesLikeLegacyString()
    {
        var view = DeserializeView("""{"sdi": "drawer"}""");

        view.Display.ShouldBe("drawer");
        view.MdiDisplay.ShouldBeNull();
    }

    // ── Reading: absent / empty ──────────────────────────────────────────────

    [Fact]
    public void Read_Null_YieldsNoDisplayModes()
    {
        var view = DeserializeView("null");

        view.DisplayModes.ShouldBeNull();
        view.Display.ShouldBe(string.Empty);
    }

    [Fact]
    public void Read_EmptyString_YieldsNoDisplayModes()
    {
        var view = DeserializeView("\"\"");

        view.DisplayModes.ShouldBeNull();
        view.Display.ShouldBe(string.Empty);
    }

    [Fact]
    public void Read_EmptyObject_YieldsNoDisplayModes()
    {
        var view = DeserializeView("{}");

        view.DisplayModes.ShouldBeNull();
    }

    [Fact]
    public void Read_MissingDisplayProperty_YieldsNoDisplayModes()
    {
        var json = """{"type": 1, "content": "{}"}""";
        var view = JsonSerializer.Deserialize<View>(json, JsonSerializerConstants.JsonOptions);

        view.ShouldNotBeNull();
        view.DisplayModes.ShouldBeNull();
        view.Display.ShouldBe(string.Empty);
    }

    // ── Writing: round-trip ──────────────────────────────────────────────────

    [Fact]
    public void Write_SdiOnly_RoundTripsAsBareString()
    {
        var display = SerializeDisplay(DeserializeView("\"popup\"").DisplayModes);

        display.ValueKind.ShouldBe(JsonValueKind.String);
        display.GetString().ShouldBe("popup");
    }

    [Fact]
    public void Write_WithMdi_RoundTripsAsObject()
    {
        var display = SerializeDisplay(DeserializeView("""{"sdi": "popup", "mdi": "tab"}""").DisplayModes);

        display.ValueKind.ShouldBe(JsonValueKind.Object);
        display.GetProperty("sdi").GetString().ShouldBe("popup");
        display.GetProperty("mdi").GetString().ShouldBe("tab");
    }

    [Fact]
    public void Write_MdiOnly_OmitsSdi()
    {
        var display = SerializeDisplay(DeserializeView("""{"mdi": "window"}""").DisplayModes);

        display.ValueKind.ShouldBe(JsonValueKind.Object);
        display.TryGetProperty("sdi", out _).ShouldBeFalse();
        display.GetProperty("mdi").GetString().ShouldBe("window");
    }

    [Fact]
    public void Write_Empty_WritesNull()
    {
        SerializeDisplay(null).ValueKind.ShouldBe(JsonValueKind.Null);
        SerializeDisplay(new ViewDisplay(null, null)).ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void RoundTrip_ObjectForm_PreservesBothModes()
    {
        var original = DeserializeView("""{"sdi": "bottom-sheet", "mdi": "split"}""").DisplayModes;

        var json = JsonSerializer.Serialize(original, ConverterOptions);
        var reparsed = JsonSerializer.Deserialize<ViewDisplay>(json, ConverterOptions);

        reparsed.ShouldNotBeNull();
        reparsed.Sdi.ShouldBe("bottom-sheet");
        reparsed.Mdi.ShouldBe("split");
    }

    [Fact]
    public void RoundTrip_LegacyStringForm_StaysAStringAndReparsesToSdi()
    {
        var original = DeserializeView("\"drawer\"").DisplayModes;

        var json = JsonSerializer.Serialize(original, ConverterOptions);
        json.ShouldBe("\"drawer\"");

        var reparsed = JsonSerializer.Deserialize<ViewDisplay>(json, ConverterOptions);
        reparsed.ShouldNotBeNull();
        reparsed.Sdi.ShouldBe("drawer");
        reparsed.Mdi.ShouldBeNull();
    }

    // ── ViewDisplay value object ─────────────────────────────────────────────

    [Fact]
    public void IsEmpty_WhenBothModesBlank_IsTrue()
    {
        new ViewDisplay(null, null).IsEmpty.ShouldBeTrue();
        new ViewDisplay("  ", "").IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void IsEmpty_WhenEitherModeSet_IsFalse()
    {
        new ViewDisplay("popup", null).IsEmpty.ShouldBeFalse();
        new ViewDisplay(null, "tab").IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void FromSdi_SetsOnlySdi()
    {
        var display = ViewDisplay.FromSdi("inline");

        display.Sdi.ShouldBe("inline");
        display.Mdi.ShouldBeNull();
    }
}
