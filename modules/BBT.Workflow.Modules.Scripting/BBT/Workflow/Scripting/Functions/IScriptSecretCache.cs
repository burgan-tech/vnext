using System.Collections.Generic;
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
}
