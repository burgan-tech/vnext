namespace BBT.Workflow.Middlewares;

/// <summary>How the Dapr local-delivery guard reacts to a delivery that fails the rule.</summary>
public enum DaprLocalDeliveryMode
{
    /// <summary>The guard does nothing.</summary>
    Off,

    /// <summary>Failing deliveries are logged but still served.</summary>
    LogOnly,

    /// <summary>Failing deliveries are rejected.</summary>
    Enforce
}

/// <summary>
/// Options for the guard that lets Dapr-triggered endpoints accept only deliveries from the
/// domain's own sidecar. Binds from <c>Dapr:LocalDelivery</c>.
/// </summary>
public sealed class DaprLocalDeliveryOptions
{
    /// <summary>appsettings section this class binds to.</summary>
    public const string SectionPath = "Dapr:LocalDelivery";

    /// <summary>Guard mode. Default: <see cref="DaprLocalDeliveryMode.LogOnly"/>.</summary>
    public DaprLocalDeliveryMode Mode { get; set; } = DaprLocalDeliveryMode.LogOnly;
}
