using System;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// Marks an endpoint that Dapr triggers by delivering to the app (pub/sub, input binding, job).
/// <see cref="DaprLocalDeliveryMiddleware"/> accepts such an endpoint only from the domain's own
/// sidecar. Metadata only; it has no behaviour of its own.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class DaprLocalDeliveryOnlyAttribute : Attribute
{
}
