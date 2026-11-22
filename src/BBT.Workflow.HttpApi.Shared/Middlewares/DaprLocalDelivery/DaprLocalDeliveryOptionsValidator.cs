using BBT.Workflow.RuntimeCaller;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// Fails startup (via <c>ValidateOnStart</c>) when the guard is set to
/// <see cref="DaprLocalDeliveryMode.Enforce"/> without <c>Workflow:RuntimeCaller:AppApiTokenFile</c>:
/// such a pod would look healthy while rejecting every Dapr-triggered delivery. The file itself is
/// validated by the runtime-caller validator (a set path with a missing or empty file already fails
/// startup), so only the path is checked here.
/// </summary>
public sealed class DaprLocalDeliveryOptionsValidator(IOptions<RuntimeCallerOptions> runtimeCaller)
    : IValidateOptions<DaprLocalDeliveryOptions>
{
    /// <summary>Fails when Enforce is set without a token file path.</summary>
    public ValidateOptionsResult Validate(string? name, DaprLocalDeliveryOptions options) =>
        options.Mode == DaprLocalDeliveryMode.Enforce
        && string.IsNullOrWhiteSpace(runtimeCaller.Value.AppApiTokenFile)
            ? ValidateOptionsResult.Fail(
                "Dapr:LocalDelivery:Mode=Enforce requires Workflow:RuntimeCaller:AppApiTokenFile; "
                + "without it every Dapr-triggered delivery would be rejected.")
            : ValidateOptionsResult.Success;
}
