using BBT.Workflow.Monitor.Components.DTOs;
using BBT.Workflow.Monitor.Components.Filters;
using Shouldly;

namespace BBT.Workflow.Monitor.Application.Tests.Components;

public sealed class ComponentFilterDescriptorTests
{
    // AllowedFor — common fields present for every type
    [Theory]
    [InlineData(MonitorComponentTypes.Flows)]
    [InlineData(MonitorComponentTypes.Tasks)]
    [InlineData(MonitorComponentTypes.Schemas)]
    [InlineData(MonitorComponentTypes.Views)]
    [InlineData(MonitorComponentTypes.Functions)]
    [InlineData(MonitorComponentTypes.Extensions)]
    [InlineData(MonitorComponentTypes.Mappings)]
    public void AllowedFor_AlwaysContainsCommonFields(string componentType)
    {
        var allowed = ComponentFilterDescriptor.AllowedFor(componentType);

        allowed.ShouldContain("createdAt");
        allowed.ShouldContain("modifiedAt");
        allowed.ShouldContain("tags");
        allowed.ShouldContain("flowVersion");
        allowed.ShouldContain("key");
        allowed.ShouldContain("version");
    }

    [Fact]
    public void AllowedFor_Views_ContainsRendererAndDisplay()
    {
        var allowed = ComponentFilterDescriptor.AllowedFor(MonitorComponentTypes.Views);

        allowed.ShouldContain("renderer");
        allowed.ShouldContain("display");
        allowed.ShouldContain("definitionType");
    }

    [Fact]
    public void AllowedFor_Views_DoesNotContainScope()
    {
        var allowed = ComponentFilterDescriptor.AllowedFor(MonitorComponentTypes.Views);
        allowed.ShouldNotContain("scope");
    }

    [Fact]
    public void AllowedFor_Functions_ContainsScopeOnly()
    {
        var allowed = ComponentFilterDescriptor.AllowedFor(MonitorComponentTypes.Functions);

        allowed.ShouldContain("scope");
        allowed.ShouldNotContain("renderer");
        allowed.ShouldNotContain("display");
        allowed.ShouldNotContain("definitionType");
        allowed.ShouldNotContain("name");
    }

    [Fact]
    public void AllowedFor_Mappings_ContainsNameOnly()
    {
        var allowed = ComponentFilterDescriptor.AllowedFor(MonitorComponentTypes.Mappings);

        allowed.ShouldContain("name");
        allowed.ShouldNotContain("scope");
        allowed.ShouldNotContain("renderer");
        allowed.ShouldNotContain("definitionType");
    }

    [Fact]
    public void AllowedFor_Extensions_ContainsDefinitionTypeAndScope()
    {
        var allowed = ComponentFilterDescriptor.AllowedFor(MonitorComponentTypes.Extensions);

        allowed.ShouldContain("definitionType");
        allowed.ShouldContain("scope");
        allowed.ShouldNotContain("renderer");
    }

    // FindDisallowed — returns empty when filter is valid
    [Fact]
    public void FindDisallowed_ViewsWithRenderer_ReturnsEmpty()
    {
        var filter = new MonitorComponentFilterInput { Renderer = "default" };
        var result = ComponentFilterDescriptor.FindDisallowed(MonitorComponentTypes.Views, filter);
        result.ShouldBeEmpty();
    }

    // FindDisallowed — returns disallowed fields
    [Fact]
    public void FindDisallowed_FlowsWithRenderer_ReturnsRenderer()
    {
        var filter = new MonitorComponentFilterInput { Renderer = "default" };
        var result = ComponentFilterDescriptor.FindDisallowed(MonitorComponentTypes.Flows, filter);
        result.ShouldContain("renderer");
    }

    [Fact]
    public void FindDisallowed_FlowsWithMultipleInvalidFields_ReturnsAll()
    {
        var filter = new MonitorComponentFilterInput { Renderer = "default", Display = "form", Scope = "global" };
        var result = ComponentFilterDescriptor.FindDisallowed(MonitorComponentTypes.Flows, filter);
        result.ShouldContain("renderer");
        result.ShouldContain("display");
        result.ShouldContain("scope");
    }

    [Fact]
    public void FindDisallowed_EmptyFilter_ReturnsEmpty()
    {
        var filter = new MonitorComponentFilterInput();
        var result = ComponentFilterDescriptor.FindDisallowed(MonitorComponentTypes.Flows, filter);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void FindDisallowed_CommonFieldsOnAnyType_ReturnsEmpty()
    {
        var filter = new MonitorComponentFilterInput
        {
            CreatedAtGte  = DateTime.UtcNow.AddDays(-7),
            TagsContains  = "production",
            KeyContains   = "order",
        };
        var result = ComponentFilterDescriptor.FindDisallowed(MonitorComponentTypes.Mappings, filter);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void FindDisallowed_KeyOnAnyType_ReturnsEmpty()
    {
        var filter = new MonitorComponentFilterInput { KeyEq = "my-flow" };
        var result = ComponentFilterDescriptor.FindDisallowed(MonitorComponentTypes.Flows, filter);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void FindDisallowed_VersionOnAnyType_ReturnsEmpty()
    {
        var filter = new MonitorComponentFilterInput { VersionContains = "1.0" };
        var result = ComponentFilterDescriptor.FindDisallowed(MonitorComponentTypes.Tasks, filter);
        result.ShouldBeEmpty();
    }
}
