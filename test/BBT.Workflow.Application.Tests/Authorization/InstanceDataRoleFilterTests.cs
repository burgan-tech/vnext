using System;
using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Authorization;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Unit tests for InstanceDataRoleFilter (filter instance data by visible paths).
/// </summary>
public sealed class InstanceDataRoleFilterTests
{
    [Fact]
    public void FilterByVisiblePaths_WhenNoPathsWithRoles_ReturnsOriginal()
    {
        var data = JsonDocument.Parse(@"{""amount"": 100, ""publicStatus"": ""active""}").RootElement;
        var pathsWithRoles = new HashSet<string>();
        var visiblePaths = new HashSet<string>();
        var result = InstanceDataRoleFilter.FilterByVisiblePaths(data, pathsWithRoles, visiblePaths);
        result.ValueKind.ShouldBe(JsonValueKind.Object);
        result.GetProperty("amount").GetDouble().ShouldBe(100);
        result.GetProperty("publicStatus").GetString().ShouldBe("active");
    }

    [Fact]
    public void FilterByVisiblePaths_WhenPathNotVisible_RemovesProperty()
    {
        var data = JsonDocument.Parse(@"{""amount"": 100, ""internalNotes"": ""secret"", ""publicStatus"": ""active""}").RootElement;
        var pathsWithRoles = new HashSet<string>(StringComparer.Ordinal) { "amount", "internalNotes" };
        var visiblePaths = new HashSet<string>(StringComparer.Ordinal) { "amount" };
        var result = InstanceDataRoleFilter.FilterByVisiblePaths(data, pathsWithRoles, visiblePaths);
        result.TryGetProperty("amount", out _).ShouldBeTrue();
        result.TryGetProperty("internalNotes", out _).ShouldBeFalse();
        result.TryGetProperty("publicStatus", out _).ShouldBeTrue();
        result.GetProperty("amount").GetDouble().ShouldBe(100);
        result.GetProperty("publicStatus").GetString().ShouldBe("active");
    }

    [Fact]
    public void FilterByVisiblePaths_WhenNestedPathNotVisible_RemovesNestedProperty()
    {
        var data = JsonDocument.Parse(@"{
            ""nested"": { ""foo"": ""a"", ""bar"": ""b"" },
            ""top"": ""keep""
        }").RootElement;
        var pathsWithRoles = new HashSet<string>(StringComparer.Ordinal) { "nested", "nested.foo", "nested.bar" };
        var visiblePaths = new HashSet<string>(StringComparer.Ordinal) { "nested", "nested.foo" };
        var result = InstanceDataRoleFilter.FilterByVisiblePaths(data, pathsWithRoles, visiblePaths);
        result.TryGetProperty("top", out _).ShouldBeTrue();
        result.TryGetProperty("nested", out var nested).ShouldBeTrue();
        nested.TryGetProperty("foo", out _).ShouldBeTrue();
        nested.TryGetProperty("bar", out _).ShouldBeFalse();
    }

    private static readonly BBT.Workflow.Definitions.Schemas.FieldMaskRule MaskAll =
        new(BBT.Workflow.Definitions.Schemas.FieldMaskRule.MaskOperator, "*", 0, 0, null, []);

    [Fact]
    public void Apply_MasksStringLeaves_InObjectsAndArraysOfObjects_AndLeavesTheRestUntouched()
    {
        var data = JsonDocument.Parse("""
            { "iban": "TR33", "count": 7, "cards": [ { "pan": "4111" }, { "pan": "5500" } ], "public": "ok" }
            """).RootElement;
        var rules = new Dictionary<string, BBT.Workflow.Definitions.Schemas.FieldMaskRule>(StringComparer.Ordinal)
        {
            ["iban"] = MaskAll,
            ["cards.pan"] = MaskAll
        };

        var result = InstanceDataRoleFilter.Apply(data, new HashSet<string>(), new HashSet<string>(), rules, new FakeFieldMaskingEngine());

        result.GetProperty("iban").GetString().ShouldBe("****");
        result.GetProperty("count").GetInt32().ShouldBe(7);
        result.GetProperty("cards")[0].GetProperty("pan").GetString().ShouldBe("****");
        result.GetProperty("cards")[1].GetProperty("pan").GetString().ShouldBe("****");
        result.GetProperty("public").GetString().ShouldBe("ok");
    }

    [Fact]
    public void Apply_HiddenPathWithAMaskRule_IsPrunedAndTheEngineIsNeverCalled()
    {
        var data = JsonDocument.Parse("""{ "secret": "top", "nested": { "inner": "x" } }""").RootElement;
        var rules = new Dictionary<string, BBT.Workflow.Definitions.Schemas.FieldMaskRule>(StringComparer.Ordinal)
        {
            ["secret"] = MaskAll,
            ["nested.inner"] = MaskAll
        };
        var engine = new FakeFieldMaskingEngine();

        var result = InstanceDataRoleFilter.Apply(
            data, new HashSet<string>(StringComparer.Ordinal) { "secret", "nested" }, new HashSet<string>(), rules, engine);

        result.TryGetProperty("secret", out _).ShouldBeFalse();
        result.TryGetProperty("nested", out _).ShouldBeFalse();
        engine.Calls.ShouldBe(0);
    }

    [Fact]
    public void Apply_NonStringValueUnderARule_IsMaskedAsText_NotLeaked_AndNullStaysNull()
    {
        var data = JsonDocument.Parse("""{ "a": 12345, "b": null, "c": true }""").RootElement;
        var rules = new Dictionary<string, BBT.Workflow.Definitions.Schemas.FieldMaskRule>(StringComparer.Ordinal)
        {
            ["a"] = MaskAll, ["b"] = MaskAll, ["c"] = MaskAll
        };

        var result = InstanceDataRoleFilter.Apply(data, new HashSet<string>(), new HashSet<string>(), rules, new FakeFieldMaskingEngine());

        result.GetProperty("a").GetString().ShouldBe("*****");
        result.GetProperty("b").ValueKind.ShouldBe(JsonValueKind.Null);
        result.GetProperty("c").GetString().ShouldBe("****");
    }
}
