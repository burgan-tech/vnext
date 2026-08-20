using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting.Functions;

/// <summary>
/// Process-wide, short-TTL, single-flight cache of Dapr secret bundles.
///
/// Registered as a singleton on purpose: <see cref="ScriptServices"/> is scoped, so cache state
/// living there would die with every request scope. In-process on purpose: secret material must
/// never transit Redis or the wire (see <see cref="SecretCacheOptions"/>).
///
/// Concurrency model: a <see cref="Lazy{T}"/>-of-<see cref="Task{T}"/> per bundle collapses a
/// thundering herd into exactly one vault round-trip. A faulted fetch is evicted immediately and
/// never cached (no negative caching); expiry is checked lazily on read against an injectable
/// <see cref="TimeProvider"/>, so no background timer is needed. The key space — the distinct
/// <c>(storeName, secretStore)</c> pairs referenced by deployed scripts — is inherently small,
/// so no entry bound or LRU is applied.
/// </summary>
public sealed class ScriptSecretCache(
    DaprClient daprClient,
    SecretCacheOptions options,
    TimeProvider timeProvider,
    ILogger<ScriptSecretCache> logger) : IScriptSecretCache
{
    private readonly DaprClient _daprClient = daprClient ?? throw new ArgumentNullException(nameof(daprClient));
    private readonly SecretCacheOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<ScriptSecretCache> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly ConcurrentDictionary<string, Lazy<Task<CachedBundle>>> _bundles = new();

    private readonly record struct CachedBundle(Dictionary<string, string> Values, DateTimeOffset ExpiresAt);

    private bool BypassCache => !_options.Enabled || _options.TtlSeconds <= 0;

    // Unit separator cannot appear in valid store/bundle names, so the composite key is unambiguous.
    private static string CacheKey(string storeName, string secretStore)
        => string.Concat(storeName, "\u001f", secretStore);

    /// <inheritdoc />
    public async Task<string> GetSecretAsync(string storeName, string secretStore, string secretKey)
    {
        var values = await GetBundleAsync(storeName, secretStore).ConfigureAwait(false);
        return values.TryGetValue(secretKey, out var value) ? value : string.Empty;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> GetSecretsAsync(string storeName, string secretStore)
        => new(await GetBundleAsync(storeName, secretStore).ConfigureAwait(false));

    private async Task<Dictionary<string, string>> GetBundleAsync(string storeName, string secretStore)
    {
        if (BypassCache)
        {
            return await FetchAsync(storeName, secretStore).ConfigureAwait(false);
        }

        var key = CacheKey(storeName, secretStore);
        while (true)
        {
            var lazy = _bundles.GetOrAdd(key, _ => new Lazy<Task<CachedBundle>>(
                () => FetchBundleAsync(storeName, secretStore),
                LazyThreadSafetyMode.ExecutionAndPublication));

            CachedBundle bundle;
            try
            {
                bundle = await lazy.Value.ConfigureAwait(false);
            }
            catch
            {
                // Evict only this exact faulted lazy (KeyValuePair overload = reference equality),
                // so a concurrently installed healthy entry is never clobbered.
                _bundles.TryRemove(new KeyValuePair<string, Lazy<Task<CachedBundle>>>(key, lazy));
                throw;
            }

            if (bundle.ExpiresAt > _timeProvider.GetUtcNow())
            {
                return bundle.Values;
            }

            // Expired: compare-and-remove this entry, then loop — the next GetOrAdd installs a
            // fresh fetch, which stays single-flight for the refresh as well.
            _bundles.TryRemove(new KeyValuePair<string, Lazy<Task<CachedBundle>>>(key, lazy));
        }
    }

    private async Task<CachedBundle> FetchBundleAsync(string storeName, string secretStore)
    {
        var values = await FetchAsync(storeName, secretStore).ConfigureAwait(false);
        // Stamp expiry after the fetch completes so the TTL measures freshness, not fetch start.
        return new CachedBundle(values, _timeProvider.GetUtcNow().AddSeconds(_options.TtlSeconds));
    }

    private async Task<Dictionary<string, string>> FetchAsync(string storeName, string secretStore)
    {
        try
        {
            var response = await _daprClient.GetSecretAsync(storeName, secretStore).ConfigureAwait(false);
            ScriptSecretCacheLogs.SecretBundleFetched(_logger, storeName, secretStore, response?.Count ?? 0);
            return response ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            ScriptSecretCacheLogs.SecretBundleFetchFailed(_logger, ex, storeName, secretStore);
            throw;
        }
    }
}
