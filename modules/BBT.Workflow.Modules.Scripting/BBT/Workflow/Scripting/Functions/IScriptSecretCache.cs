using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace BBT.Workflow.Scripting.Functions;

/// <summary>
/// Read-through cache over Dapr secret store bundle reads for script secret functions.
/// One vault round-trip fetches the whole bundle for a <c>(storeName, secretStore)</c> pair;
/// individual secret lookups are then served from the cached bundle until its TTL expires.
///
/// No <see cref="System.Threading.CancellationToken"/> is exposed: the underlying fetch is
/// shared by all concurrent callers (single-flight), so one caller's token must not cancel it.
/// </summary>
public interface IScriptSecretCache
{
    /// <summary>
    /// Gets a single secret value from the (possibly cached) bundle.
    /// Returns <see cref="string.Empty"/> when the key is absent from the bundle.
    /// </summary>
    /// <param name="storeName">The name of the Dapr secret store component.</param>
    /// <param name="secretStore">The name of the secret bundle within the store.</param>
    /// <param name="secretKey">The key of the secret inside the bundle.</param>
    Task<string> GetSecretAsync(string storeName, string secretStore, string secretKey);

    /// <summary>
    /// Gets the whole secret bundle as a defensive copy (callers may mutate the result freely).
    /// Returns an empty dictionary when the store responds with no data.
    /// </summary>
    /// <param name="storeName">The name of the Dapr secret store component.</param>
    /// <param name="secretStore">The name of the secret bundle within the store.</param>
    Task<Dictionary<string, string>> GetSecretsAsync(string storeName, string secretStore);

    /// <summary>
    /// Lock-free L1 probe for synchronous callers: returns <c>true</c> only when a fresh,
    /// successfully fetched bundle is already cached. Never blocks and never triggers a fetch,
    /// so a sync caller can serve hits without any sync-over-async hazard and fall back to the
    /// async path only on a miss. <paramref name="value"/> is <see cref="string.Empty"/> when
    /// the cached bundle lacks the key (same contract as <see cref="GetSecretAsync"/>).
    /// </summary>
    /// <param name="storeName">The name of the Dapr secret store component.</param>
    /// <param name="secretStore">The name of the secret bundle within the store.</param>
    /// <param name="secretKey">The key of the secret inside the bundle.</param>
    /// <param name="value">The secret value on a hit; <see cref="string.Empty"/> otherwise.</param>
    bool TryGetCachedSecret(string storeName, string secretStore, string secretKey, out string value);

    /// <summary>
    /// Same lock-free probe as <see cref="TryGetCachedSecret"/>, returning a defensive copy of
    /// the whole cached bundle on a hit. Never blocks and never triggers a fetch.
    /// </summary>
    /// <param name="storeName">The name of the Dapr secret store component.</param>
    /// <param name="secretStore">The name of the secret bundle within the store.</param>
    /// <param name="bundle">A defensive copy of the cached bundle on a hit; <c>null</c> otherwise.</param>
    bool TryGetCachedBundle(string storeName, string secretStore, [NotNullWhen(true)] out Dictionary<string, string>? bundle);
}
