using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Monitor.Components.DTOs;
using BBT.Workflow.Monitor.Components.Filters;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Monitor.Application.Tests.Components;

public sealed class MonitorComponentFilterTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static MonitorComponentSummaryItem Item(
        DateTime? createdAt = null,
        DateTime? modifiedAt = null,
        List<string>? tags = null,
        string? flowVersion = null,
        string? typeValue = null,
        string? display = null,
        string? renderer = null,
        string? scope = null,
        string? name = null,
        string? key = null,
        string? version = null)
    {
        JsonElement? typeEl = typeValue is null
            ? null
            : JsonDocument.Parse($"\"{typeValue}\"").RootElement.Clone();

        return new MonitorComponentSummaryItem
        {
            CreatedAt   = createdAt,
            ModifiedAt  = modifiedAt,
            Tags        = tags,
            FlowVersion = flowVersion,
            Type        = typeEl,
            Display     = display,
            Renderer    = renderer,
            Scope       = scope,
            Name        = name,
            Key         = key,
            Version     = version,
        };
    }

    // ── createdAt ───────────────────────────────────────────────────────────

    [Fact]
    public void Apply_CreatedAtGte_ExcludesOlderItems()
    {
        var boundary = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(createdAt: boundary.AddDays(-1)),
            Item(createdAt: boundary),
            Item(createdAt: boundary.AddDays(1)),
        };
        var filter = new MonitorComponentFilterInput { CreatedAtGte = boundary };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(2);
        result.ShouldAllBe(x => x.CreatedAt >= boundary);
    }

    [Fact]
    public void Apply_CreatedAtLte_ExcludesNewerItems()
    {
        var boundary = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(createdAt: boundary.AddDays(-1)),
            Item(createdAt: boundary),
            Item(createdAt: boundary.AddDays(1)),
        };
        var filter = new MonitorComponentFilterInput { CreatedAtLte = boundary };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(2);
        result.ShouldAllBe(x => x.CreatedAt <= boundary);
    }

    [Fact]
    public void Apply_CreatedAtRange_ReturnsOnlyItemsInRange()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(createdAt: from.AddDays(-1)),
            Item(createdAt: from),
            Item(createdAt: to),
            Item(createdAt: to.AddDays(1)),
        };
        var filter = new MonitorComponentFilterInput { CreatedAtGte = from, CreatedAtLte = to };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(2);
    }

    // ── modifiedAt ──────────────────────────────────────────────────────────

    [Fact]
    public void Apply_ModifiedAtGte_FiltersCorrectly()
    {
        var boundary = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(modifiedAt: boundary.AddDays(-1)),
            Item(modifiedAt: boundary),
        };
        var filter = new MonitorComponentFilterInput { ModifiedAtGte = boundary };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(1);
        result[0].ModifiedAt.ShouldBe(boundary);
    }

    // ── tags ────────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_TagsContains_CaseInsensitiveListMatch()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(tags: ["Production", "critical"]),
            Item(tags: ["staging"]),
            Item(tags: null),
        };
        var filter = new MonitorComponentFilterInput { TagsContains = "production" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Apply_TagsContains_NullTagList_Excluded()
    {
        var items = new List<MonitorComponentSummaryItem> { Item(tags: null) };
        var filter = new MonitorComponentFilterInput { TagsContains = "any" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.ShouldBeEmpty();
    }

    // ── flowVersion ─────────────────────────────────────────────────────────

    [Fact]
    public void Apply_FlowVersionEq_ExactMatchCaseInsensitive()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(flowVersion: "1.0.0"),
            Item(flowVersion: "2.0.0"),
            Item(flowVersion: null),
        };
        var filter = new MonitorComponentFilterInput { FlowVersionEq = "1.0.0" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Apply_FlowVersionContains_PartialMatchCaseInsensitive()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(flowVersion: "1.0.0"),
            Item(flowVersion: "1.0.5"),
            Item(flowVersion: "2.0.0"),
            Item(flowVersion: null),
        };
        var filter = new MonitorComponentFilterInput { FlowVersionContains = "1.0" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(2);
    }

    // ── definitionType ──────────────────────────────────────────────────────

    [Fact]
    public void Apply_DefinitionType_MatchesStringTypeElement()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(typeValue: "F"),
            Item(typeValue: "S"),
            Item(),
        };
        var filter = new MonitorComponentFilterInput { DefinitionType = "F" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Apply_DefinitionType_CaseInsensitive()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(typeValue: "flow"),
        };
        var filter = new MonitorComponentFilterInput { DefinitionType = "FLOW" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Apply_DefinitionType_NullType_Excluded()
    {
        var items = new List<MonitorComponentSummaryItem> { Item() };
        var filter = new MonitorComponentFilterInput { DefinitionType = "F" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.ShouldBeEmpty();
    }

    // ── display / renderer ──────────────────────────────────────────────────

    [Fact]
    public void Apply_Display_ExactMatchCaseInsensitive()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(display: "form"),
            Item(display: "list"),
        };
        var filter = new MonitorComponentFilterInput { Display = "FORM" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(1);
    }

    [Fact]
    public void Apply_Renderer_ExactMatchCaseInsensitive()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(renderer: "default"),
            Item(renderer: "custom"),
            Item(renderer: null),
        };
        var filter = new MonitorComponentFilterInput { Renderer = "default" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(1);
    }

    // ── scope ───────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_Scope_ExactMatchCaseInsensitive()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(scope: "global"),
            Item(scope: "domain"),
        };
        var filter = new MonitorComponentFilterInput { Scope = "GLOBAL" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(1);
    }

    // ── name ────────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_NameEq_ExactMatchCaseInsensitive()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(name: "Order Processing Scripts"),
            Item(name: "order processing scripts"),
            Item(name: "Payment Scripts"),
            Item(name: null),
        };
        var filter = new MonitorComponentFilterInput { NameEq = "Order Processing Scripts" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void Apply_NameContains_PartialMatchCaseInsensitive()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(name: "Order Processing Scripts"),
            Item(name: "Payment Scripts"),
            Item(name: null),
        };
        var filter = new MonitorComponentFilterInput { NameContains = "order" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(1);
    }

    // ── key ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_KeyEq_ExactMatchCaseInsensitive()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(key: "order-approval-flow"),
            Item(key: "ORDER-APPROVAL-FLOW"),
            Item(key: "payment-flow"),
            Item(key: null),
        };
        var filter = new MonitorComponentFilterInput { KeyEq = "order-approval-flow" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void Apply_KeyContains_PartialMatchCaseInsensitive()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(key: "order-approval-flow"),
            Item(key: "order-payment-flow"),
            Item(key: "leave-approval-flow"),
            Item(key: null),
        };
        var filter = new MonitorComponentFilterInput { KeyContains = "order" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(2);
    }

    // ── version ─────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_VersionEq_ExactMatchCaseInsensitive()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(version: "1.0.0"),
            Item(version: "1.0.0"),
            Item(version: "2.0.0"),
            Item(version: null),
        };
        var filter = new MonitorComponentFilterInput { VersionEq = "1.0.0" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(2);
        result.ShouldAllBe(x => string.Equals(x.Version, "1.0.0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_VersionContains_PartialMatchCaseInsensitive()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(version: "1.0.0"),
            Item(version: "1.0.5"),
            Item(version: "2.0.0"),
            Item(version: null),
        };
        var filter = new MonitorComponentFilterInput { VersionContains = "1.0" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void Apply_VersionEq_NullVersion_Excluded()
    {
        var items = new List<MonitorComponentSummaryItem> { Item(version: null) };
        var filter = new MonitorComponentFilterInput { VersionEq = "1.0.0" };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.ShouldBeEmpty();
    }

    // ── empty filter ────────────────────────────────────────────────────────

    [Fact]
    public void Apply_EmptyFilter_ReturnsAllItems()
    {
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(createdAt: DateTime.UtcNow),
            Item(createdAt: DateTime.UtcNow),
        };
        var filter = new MonitorComponentFilterInput();

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(2);
    }

    // ── combined filters ────────────────────────────────────────────────────

    [Fact]
    public void Apply_MultipleFilters_AllMustMatch()
    {
        var now = DateTime.UtcNow;
        var items = new List<MonitorComponentSummaryItem>
        {
            Item(createdAt: now, renderer: "default", tags: ["production"]),
            Item(createdAt: now, renderer: "default", tags: ["staging"]),
            Item(createdAt: now.AddDays(-10), renderer: "default"),
        };
        var filter = new MonitorComponentFilterInput
        {
            CreatedAtGte  = now.AddDays(-1),
            Renderer      = "default",
            TagsContains  = "production",
        };

        var result = MonitorComponentFilter.Apply(items, filter).ToList();

        result.Count.ShouldBe(1);
    }
}
