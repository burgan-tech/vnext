using System;
using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Definitions.Tasks;

/// <summary>
/// Unit tests for AddHeader and RemoveHeader on tasks that support Headers.
/// </summary>
public class TaskHeadersTests
{
    private static Dictionary<string, string?> GetHeadersDictionary(HttpTask task)
    {
        if (!task.Headers.HasValue)
            return new Dictionary<string, string?>();
        var json = task.Headers.Value.GetRawText();
        return JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
               ?? new Dictionary<string, string?>();
    }

    [Fact]
    public void AddHeader_WhenHeadersIsNull_ShouldAddNewHeader()
    {
        var task = HttpTask.CreateEmpty();

        task.AddHeader("X-Custom", "value");

        var headers = GetHeadersDictionary(task);
        Assert.Single(headers);
        Assert.Equal("value", headers["X-Custom"]);
    }

    [Fact]
    public void AddHeader_WhenKeyExists_ShouldOverrideValue()
    {
        var task = HttpTask.CreateEmpty();
        task.SetHeaders(new Dictionary<string, string?> { ["X-Foo"] = "original" });

        task.AddHeader("X-Foo", "overridden");

        var headers = GetHeadersDictionary(task);
        Assert.Single(headers);
        Assert.Equal("overridden", headers["X-Foo"]);
    }

    [Fact]
    public void AddHeader_WhenHeadersAlreadyHasEntries_ShouldAddNewKey()
    {
        var task = HttpTask.CreateEmpty();
        task.SetHeaders(new Dictionary<string, string?> { ["A"] = "a" });

        task.AddHeader("B", "b");

        var headers = GetHeadersDictionary(task);
        Assert.Equal(2, headers.Count);
        Assert.Equal("a", headers["A"]);
        Assert.Equal("b", headers["B"]);
    }

    [Fact]
    public void AddHeader_ShouldThrowWhenKeyIsNull()
    {
        var task = HttpTask.CreateEmpty();
        Assert.ThrowsAny<ArgumentException>(() => task.AddHeader(null!, "value"));
    }

    [Fact]
    public void AddHeader_ShouldThrowWhenKeyIsWhitespace()
    {
        var task = HttpTask.CreateEmpty();
        Assert.ThrowsAny<ArgumentException>(() => task.AddHeader("", "value"));
        Assert.ThrowsAny<ArgumentException>(() => task.AddHeader("   ", "value"));
    }

    [Fact]
    public void AddHeader_ShouldAcceptNullValue()
    {
        var task = HttpTask.CreateEmpty();
        task.AddHeader("X-Nullable", null);
        var headers = GetHeadersDictionary(task);
        Assert.Single(headers);
        Assert.Null(headers["X-Nullable"]);
    }

    [Fact]
    public void RemoveHeader_WhenKeyExists_ShouldRemoveIt()
    {
        var task = HttpTask.CreateEmpty();
        task.SetHeaders(new Dictionary<string, string?>
        {
            ["X-Remove"] = "v1",
            ["X-Keep"] = "v2"
        });

        task.RemoveHeader("X-Remove");

        var headers = GetHeadersDictionary(task);
        Assert.Single(headers);
        Assert.True(headers.ContainsKey("X-Keep"));
        Assert.Equal("v2", headers["X-Keep"]);
    }

    [Fact]
    public void RemoveHeader_WhenKeyDoesNotExist_ShouldDoNothing()
    {
        var task = HttpTask.CreateEmpty();
        task.SetHeaders(new Dictionary<string, string?> { ["X-Only"] = "v" });

        task.RemoveHeader("X-Missing");

        var headers = GetHeadersDictionary(task);
        Assert.Single(headers);
        Assert.Equal("v", headers["X-Only"]);
    }

    [Fact]
    public void RemoveHeader_WhenHeadersIsNull_ShouldDoNothing()
    {
        var task = HttpTask.CreateEmpty();
        task.RemoveHeader("X-Any");
        Assert.False(task.Headers.HasValue);
    }

    [Fact]
    public void RemoveHeader_ShouldThrowWhenKeyIsNull()
    {
        var task = HttpTask.CreateEmpty();
        Assert.ThrowsAny<ArgumentException>(() => task.RemoveHeader(null!));
    }

    [Fact]
    public void RemoveHeader_ShouldThrowWhenKeyIsWhitespace()
    {
        var task = HttpTask.CreateEmpty();
        Assert.ThrowsAny<ArgumentException>(() => task.RemoveHeader(""));
        Assert.ThrowsAny<ArgumentException>(() => task.RemoveHeader("   "));
    }

    [Fact]
    public void RemoveHeader_WhenLastHeaderRemoved_ShouldSetHeadersToNull()
    {
        var task = HttpTask.CreateEmpty();
        task.SetHeaders(new Dictionary<string, string?> { ["Only"] = "v" });

        task.RemoveHeader("Only");

        Assert.False(task.Headers.HasValue);
    }

    [Fact]
    public void AddHeader_AndRemoveHeader_RoundTrip_ShouldBeConsistent()
    {
        var task = HttpTask.CreateEmpty();
        task.AddHeader("A", "1");
        task.AddHeader("B", "2");
        task.RemoveHeader("A");
        var headers = GetHeadersDictionary(task);
        Assert.Single(headers);
        Assert.Equal("2", headers["B"]);
    }

    [Fact]
    public void GetInstancesTask_AddHeader_RemoveHeader_ShouldBehaveSameAsHttpTask()
    {
        var task = GetInstancesTask.CreateEmpty();
        task.AddHeader("X-Test", "value");
        var json = task.Headers!.Value.GetRawText();
        var dict = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
        Assert.NotNull(dict);
        Assert.Equal("value", dict["X-Test"]);
        task.RemoveHeader("X-Test");
        Assert.False(task.Headers.HasValue);
    }
}
