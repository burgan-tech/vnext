using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Monitor.Instances;
using BBT.Workflow.Monitor.Instances.DTOs;
using Xunit;

namespace BBT.Workflow.Monitor.Application.Tests;

public sealed class InstanceHierarchyBuilderTests
{
    [Fact]
    public async Task Build_StopsAtMaxDepth()
    {
        // Every node yields exactly one fresh child → infinite chain unless depth-capped.
        Func<MonitorHierarchyNode, int, Task<List<MonitorHierarchyNode>>> fetch =
            (node, depth) => Task.FromResult(new List<MonitorHierarchyNode> { new() { InstanceId = Guid.NewGuid() } });

        var root = new MonitorHierarchyNode { InstanceId = Guid.NewGuid() };
        await InstanceHierarchyBuilder.PopulateAsync(root, fetch, maxDepth: 2, visited: new HashSet<Guid> { root.InstanceId });

        // root(depth0) -> child(depth1) -> grandchild(depth2 stops, no children)
        Assert.Single(root.Children);
        Assert.Single(root.Children[0].Children);
        Assert.Empty(root.Children[0].Children[0].Children);
    }

    [Fact]
    public async Task Build_SkipsAlreadyVisited_PreventingCycles()
    {
        var a = Guid.NewGuid();
        Func<MonitorHierarchyNode, int, Task<List<MonitorHierarchyNode>>> fetch =
            (node, depth) => Task.FromResult(new List<MonitorHierarchyNode> { new() { InstanceId = a } }); // always same child

        var root = new MonitorHierarchyNode { InstanceId = a };
        await InstanceHierarchyBuilder.PopulateAsync(root, fetch, maxDepth: 10, visited: new HashSet<Guid> { a });

        Assert.Empty(root.Children); // 'a' already visited → cycle prevented
    }
}
