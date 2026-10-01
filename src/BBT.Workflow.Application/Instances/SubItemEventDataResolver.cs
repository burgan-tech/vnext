using System.Text.Json;
using BBT.Aether.MultiSchema;

namespace BBT.Workflow.Instances;

/// <summary>
/// The data a SubFlow/SubProcess hands to its parent when it completes or faults (<c>InstanceSubCompletedEvent</c> /
/// <c>InstanceSubFaultedEvent</c>). The parent's output mapping receives it as <c>context.Body</c> and can open nothing of
/// the child's — <c>DecryptAsync</c> opens only an instance's own values, and a cross-domain parent has no access to the
/// child's secret at all — so the child opens its own <c>x-encryption.type: "encrypt"</c> values here, with its own key,
/// before the event is raised.
/// </summary>
public interface ISubItemEventDataResolver
{
    /// <summary>
    /// The opened latest data of <paramref name="instance"/> when it is a sub item whose row carries tokens; otherwise
    /// <c>null</c>, meaning "the row as stored" (which then equals its plaintext). A value that cannot be opened stays a
    /// token. <paramref name="cancellationToken"/> is used for this call only.
    /// </summary>
    Task<JsonElement?> ResolveAsync(Instance instance, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISubItemEventDataResolver" />
public sealed class SubItemEventDataResolver(
    IInstanceDataProtector? protector = null,
    ICurrentSchema? currentSchema = null) : ISubItemEventDataResolver
{
    /// <inheritdoc />
    public async Task<JsonElement?> ResolveAsync(Instance instance, CancellationToken cancellationToken = default)
    {
        if (protector is null || !instance.IsSubItem)
            return null;

        var row = instance.LatestData;
        if (row is null || !EncryptedValueFormat.MayContainToken(row.Data.Json))
            return null;

        var view = await protector.UnprotectAsync(currentSchema?.Name, instance.Id, row.Data, cancellationToken);
        return view.Plain.JsonElement;
    }
}

/// <summary>Null-tolerant call for the services that take the resolver as an optional dependency.</summary>
public static class SubItemEventDataResolverExtensions
{
    /// <summary>The resolver's answer, or <c>null</c> (the row as stored) when no resolver is wired.</summary>
    public static Task<JsonElement?> ResolveOrStoredAsync(
        this ISubItemEventDataResolver? resolver, Instance instance, CancellationToken cancellationToken) =>
        resolver is null ? Task.FromResult<JsonElement?>(null) : resolver.ResolveAsync(instance, cancellationToken);
}
