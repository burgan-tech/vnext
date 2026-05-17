using BBT.Workflow.Infrastructure.Dapr;
using BBT.Workflow.Infrastructure.Dapr.Notification;
using BBT.Workflow.Tasks.Notification;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Dapr.Notification;

public sealed class NotificationChannelResolverTests
{
    private static NotificationChannelResolver CreateResolver(string prefix = "vnext-notification")
    {
        var options = Options.Create(new DaprNotificationOptions { BindingPrefix = prefix });
        return new NotificationChannelResolver(options);
    }

    [Theory]
    [InlineData("sms", "vnext-notification-sms")]
    [InlineData("email", "vnext-notification-email")]
    [InlineData("push", "vnext-notification-push")]
    [InlineData("hub", "vnext-notification-hub")]
    [InlineData("state", "vnext-notification-state")]
    public void ResolveBindingName_ProducesConventionName(string channel, string expected)
    {
        var resolver = CreateResolver();
        resolver.ResolveBindingName(channel).ShouldBe(expected);
    }

    [Fact]
    public void ResolveBindingName_NormalizesToLowerCase()
    {
        var resolver = CreateResolver();
        resolver.ResolveBindingName("SMS").ShouldBe("vnext-notification-sms");
        resolver.ResolveBindingName("Email").ShouldBe("vnext-notification-email");
    }

    [Fact]
    public void ResolveBindingName_RespectsCustomPrefix()
    {
        var resolver = CreateResolver(prefix: "custom-notify");
        resolver.ResolveBindingName("sms").ShouldBe("custom-notify-sms");
    }
}
