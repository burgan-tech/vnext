using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared.Merging;
using BBT.Workflow.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Data;

/// <summary>
/// Buffered appends of a <c>history: none</c> instance (vnext#1006): merged in memory with the same
/// rules as a direct append, never touching the database until the flush.
/// </summary>
public class InstanceDataWriteServiceBufferTests
{
    private readonly IAetherDbContextProvider<WorkflowDbContext> _dbContextProvider =
        Substitute.For<IAetherDbContextProvider<WorkflowDbContext>>();

    private InstanceDataWriteService CreateService() => new(
        _dbContextProvider,
        Substitute.For<IServiceProvider>(),
        Substitute.For<IJsonSchemaValidator>(),
        Options.Create(new WorkflowExecutionOptions()),
        NullLogger<InstanceDataWriteService>.Instance);

    [Fact]
    public async Task BufferedAppends_MergeInMemory_WithoutTheDatabase()
    {
        var service = CreateService();
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();

        await service.AppendAsync(instance, new JsonData("""{"a":1}"""), VersionStrategy.IncreaseMinor);
        var row = await service.AppendAsync(instance, new JsonData("""{"b":2}"""), VersionStrategy.IncreaseMinor);

        row.ShouldNotBeNull();
        instance.LatestData.ShouldBeSameAs(row);
        Json(instance.LatestData!.Data).ShouldBe(Json("""{"a":1,"b":2}"""));
        instance.DataList.ShouldBeEmpty();
        Json(instance.DataBuffer!.AccumulatedDelta!).ShouldBe(Json("""{"a":1,"b":2}"""));
        await _dbContextProvider.DidNotReceive().GetDbContextAsync();
    }

    [Fact]
    public async Task BufferedDuplicate_ReturnsNull_AndKeepsTheHead()
    {
        var service = CreateService();
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();

        var first = await service.AppendAsync(instance, new JsonData("""{"a":1}"""), VersionStrategy.IncreaseMinor);
        await service.AppendAsync(instance, new JsonData("""{"b":2}"""), VersionStrategy.IncreaseMinor);
        var duplicate = await service.AppendAsync(instance, new JsonData("""{"b":2}"""), VersionStrategy.IncreaseMinor);

        first.ShouldNotBeNull();
        duplicate.ShouldBeNull();
    }

    [Fact]
    public async Task ParallelBranches_ShareTheBuffer_AndKeepEveryDelta()
    {
        var service = CreateService();
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();
        var left = instance.CreateSnapshot();
        var right = instance.CreateSnapshot();

        await Task.WhenAll(
            service.AppendAsync(left, new JsonData("""{"left":1}"""), VersionStrategy.IncreasePatch),
            service.AppendAsync(right, new JsonData("""{"right":2}"""), VersionStrategy.IncreasePatch));

        Json(instance.LatestData!.Data).ShouldBe(Json("""{"left":1,"right":2}"""));
    }

    [Fact]
    public async Task FlushWithoutPendingChanges_RunsOnlyTheCallback()
    {
        var service = CreateService();
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();
        var called = false;

        var row = await service.FlushAsync(instance, null, _ => { called = true; return Task.CompletedTask; });

        row.ShouldBeNull();
        called.ShouldBeTrue();
        await _dbContextProvider.DidNotReceive().GetDbContextAsync();
    }

    /// <summary>
    /// The flush merges the ACCUMULATED delta onto the persisted head; that equals appending each delta
    /// in turn only because the merge is associative. Pins it on the rules that could break it.
    /// </summary>
    [Theory]
    [InlineData("""{"a":1,"n":{"x":1}}""", """{"a":2,"n":{"y":2}}""", """{"b":3,"n":{"x":9}}""")]
    [InlineData("""{"a":1}""", """{"a":null}""", """{"b":2}""")]
    [InlineData("""{"list":[1,2,3]}""", """{"list":[4]}""", """{"list":[5,6]}""")]
    [InlineData("""{"v":{"k":1}}""", """{"v":"text"}""", """{"v":{"k":2}}""")]
    [InlineData("""{"camelCase":1}""", """{"CamelCase":2}""", """{"other":true}""")]
    public void Merge_IsAssociative_ForTheBufferedFlush(string @base, string d1, string d2)
    {
        string Merge(string left, string right) => JsonCanonicalizer.MergeAndCanonicalize(
            JsonDocument.Parse(left).RootElement, JsonDocument.Parse(right).RootElement).NormalizedJson;

        var sequential = Merge(Merge(@base, d1), d2);
        var accumulated = Merge(@base, Merge(d1, d2));

        accumulated.ShouldBe(sequential);
    }

    private static string Json(JsonData data) => Json(data.Json);

    private static string Json(string json) =>
        JsonCanonicalizer.MergeAndCanonicalize(JsonDocument.Parse("{}").RootElement, JsonDocument.Parse(json).RootElement).NormalizedJson;
}
