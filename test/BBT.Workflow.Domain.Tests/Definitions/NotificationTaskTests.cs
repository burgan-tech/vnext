using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions;

public sealed class NotificationTaskTests
{
    [Fact]
    public void Create_ParsesChannelsFromConfig()
    {
        var config = """
        {
            "key": "notify",
            "type": "14",
            "channels": ["sms", "email", "push"]
        }
        """;
        var task = NotificationTask.Create(JsonSerializer.Deserialize<JsonElement>(config));

        task.Channels.Count.ShouldBe(3);
        task.Channels.ShouldContain("sms");
        task.Channels.ShouldContain("email");
        task.Channels.ShouldContain("push");
    }

    [Fact]
    public void Create_ParsesIncludeStateChannel()
    {
        var config = """
        {
            "key": "notify",
            "type": "14",
            "channels": ["sms"],
            "includeStateChannel": true
        }
        """;
        var task = NotificationTask.Create(JsonSerializer.Deserialize<JsonElement>(config));

        task.IncludeStateChannel.ShouldBeTrue();
    }

    [Fact]
    public void Create_DefaultsIncludeStateChannelToFalse()
    {
        var config = """
        {
            "key": "notify",
            "type": "14",
            "channels": ["sms"]
        }
        """;
        var task = NotificationTask.Create(JsonSerializer.Deserialize<JsonElement>(config));

        task.IncludeStateChannel.ShouldBeFalse();
    }

    [Fact]
    public void Create_EmptyChannels_ReturnsEmptyList()
    {
        var config = """
        {
            "key": "notify",
            "type": "14",
            "channels": []
        }
        """;
        var task = NotificationTask.Create(JsonSerializer.Deserialize<JsonElement>(config));

        task.Channels.ShouldBeEmpty();
    }

    [Fact]
    public void Create_NoChannelsProperty_DefaultsToEmpty()
    {
        var config = """
        {
            "key": "notify",
            "type": "14"
        }
        """;
        var task = NotificationTask.Create(JsonSerializer.Deserialize<JsonElement>(config));

        task.Channels.ShouldBeEmpty();
    }

    [Fact]
    public void Clone_PreservesChannelsAndStateFlag()
    {
        var config = """
        {
            "key": "notify",
            "type": "14",
            "channels": ["sms", "email"],
            "includeStateChannel": true
        }
        """;
        var original = NotificationTask.Create(JsonSerializer.Deserialize<JsonElement>(config));
        original.SetReference(new Reference("notify", "test", "sys-tasks", "1.0"));
        var cloned = original.CloneTyped();

        cloned.Channels.Count.ShouldBe(2);
        cloned.IncludeStateChannel.ShouldBeTrue();
    }

    [Fact]
    public void Reset_ClearsChannelsAndStateFlag()
    {
        var config = """
        {
            "key": "notify",
            "type": "14",
            "channels": ["sms"],
            "includeStateChannel": true
        }
        """;
        var task = NotificationTask.Create(JsonSerializer.Deserialize<JsonElement>(config));
        task.Reset();

        task.Channels.ShouldBeEmpty();
        task.IncludeStateChannel.ShouldBeFalse();
    }
}
