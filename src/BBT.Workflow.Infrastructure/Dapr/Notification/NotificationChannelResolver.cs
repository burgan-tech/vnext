using BBT.Workflow.Tasks.Notification;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Infrastructure.Dapr.Notification;

/// <inheritdoc />
internal sealed class NotificationChannelResolver : INotificationChannelResolver
{
    private readonly string _prefix;

    public NotificationChannelResolver(IOptions<DaprNotificationOptions> options)
    {
        _prefix = options.Value.BindingPrefix;
    }

    public string ResolveBindingName(string channel) =>
        $"{_prefix}-{channel.ToLowerInvariant()}";
}
