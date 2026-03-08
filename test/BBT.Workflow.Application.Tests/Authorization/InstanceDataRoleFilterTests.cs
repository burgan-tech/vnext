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
}
