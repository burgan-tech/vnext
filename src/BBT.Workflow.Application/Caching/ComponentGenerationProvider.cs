using System.Collections.Concurrent;
using BBT.Aether.DistributedCache;
using BBT.Aether.Guids;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Caching;

/// <summary>
/// Redis-backed <see cref="IComponentGenerationProvider"/>. Stores one small token per component key,
/// shared by every pod, so a bump takes effect cluster-wide the moment it is written.
/// </summary>
/// <param name="distributedCache">The distributed cache holding the tokens.</param>
/// <param name="guidGenerator">Source of fresh token values.</param>
/// <param name="options">Component cache options (token TTL and optional in-process memoization).</param>
/// <param name="timeProvider">Time source for in-process memoization expiry.</param>
/// <param name="logger">Logger for bump outcomes.</param>
public sealed class ComponentGenerationProvider(
    IDistributedCacheService distributedCache,
    IGuidGenerator guidGenerator,
    IOptions<ComponentCacheOptions> options,
    TimeProvider timeProvider,
    ILogger<ComponentGenerationProvider> logger) : IComponentGenerationProvider
{
    private readonly ConcurrentDictionary<string, MemoizedToken> _memo = new();

    /// <inheritdoc />
    public async Task<string> GetAsync(
        string componentTypeKey,
        string domain,
        string key,
        CancellationToken cancellationToken = default)
    {
        var redisKey = CreateGenerationKey(componentTypeKey, domain, key);

        if (TryReadMemo(redisKey, out var memoized))
            return memoized;

        // Every component resolution reads this token before it may use a cached body, and the read
        // is a real round trip to the distributed cache. It produced no span of its own, so the cost
        // showed up as time in whatever happened to be running — the caller's Cache.Get sits AFTER
        // this, not around it, so the token read was attributed to nothing at all.
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGenerationGet, redisKey, componentTypeKey);

        try
        {
            var entry = await distributedCache.GetAsync<ComponentGenerationEntry>(redisKey, cancellationToken);
            if (!string.IsNullOrEmpty(entry?.Token))
            {
                WriteMemo(redisKey, entry.Token);
                return entry.Token;
            }
        }
        // Cancellation is excluded from every degradation branch below: fabricating a token for an
        // abandoned caller would hand it a resolution key and let it keep doing work (component
        // resolution, script compilation) nobody is waiting for. Real infrastructure failures keep
        // failing open exactly as before.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.ComponentCacheOperationFailed(ex, CacheActivityHelper.OperationGenerationGet, redisKey);

            // Deliberately not written back. A failed read is no evidence the token is absent, and
            // replacing it would invalidate every pod's resolutions for no reason — worse, if reads keep
            // failing while writes succeed, every request would do so in turn. An unshared token instead
            // degrades just this call to a backend load, which is the right answer for a cache we cannot
            // read.
            return CreateToken();
        }

        var token = CreateToken();

        try
        {
            await distributedCache.SetAsync(redisKey, new ComponentGenerationEntry(token), EntryOptions(), cancellationToken);
            logger.ComponentCacheGenerationBootstrapped(componentTypeKey, domain, key, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The token is still usable for this call; it just will not be shared with other callers,
            // so they resolve from the backend too. Correct, only slower.
            logger.ComponentCacheOperationFailed(ex, CacheActivityHelper.OperationGenerationSet, redisKey);
            return token;
        }

        WriteMemo(redisKey, token);
        return token;
    }

    /// <inheritdoc />
    public async Task<string> BumpAsync(
        string componentTypeKey,
        string domain,
        string key,
        CancellationToken cancellationToken = default)
    {
        var redisKey = CreateGenerationKey(componentTypeKey, domain, key);
        var token = CreateToken();

        // Drop the memo first: if the write below fails we must not keep serving the previous token
        // from this pod on the strength of a bump that never landed.
        _memo.TryRemove(redisKey, out _);

        try
        {
            await distributedCache.SetAsync(redisKey, new ComponentGenerationEntry(token), EntryOptions(), cancellationToken);
            logger.ComponentCacheGenerationBumped(componentTypeKey, domain, key, token);
            WriteMemo(redisKey, token);
            return token;
        }
        // A cancelled bump propagates: the write did not land, so reporting a fresh token would tell
        // the caller its publish took effect cluster-wide when it did not.
        catch (Exception writeException) when (writeException is not OperationCanceledException)
        {
            try
            {
                // Removing the token invalidates just as effectively — the next reader bootstraps a
                // token nobody's stale entries were written under.
                await distributedCache.RemoveAsync(redisKey, cancellationToken);
                logger.ComponentCacheGenerationBumpFellBackToRemove(writeException, componentTypeKey, domain, key);
            }
            // This compensation stays fully best-effort — including on cancellation. Letting an
            // OperationCanceledException out here would replace the original write failure with a
            // cancellation and lose the diagnosis; the pre-bump token simply survives (documented
            // stale window below).
            catch (Exception removeException)
            {
                // The previous token survives, so previously cached resolutions stay reachable. This is
                // the one path that can serve stale definitions, and it is bounded only by the token TTL.
                logger.ComponentCacheGenerationBumpFailed(removeException, componentTypeKey, domain, key);
            }

            return token;
        }
    }

    private string CreateToken() => guidGenerator.Create().ToString("N");

    private DistributedCacheEntryOptions EntryOptions() => new()
    {
        AbsoluteExpiration = timeProvider.GetUtcNow().AddSeconds(options.Value.GenerationTtlSeconds)
    };

    private bool TryReadMemo(string redisKey, out string token)
    {
        token = string.Empty;

        if (options.Value.GenerationMemoSeconds <= 0)
            return false;

        if (!_memo.TryGetValue(redisKey, out var entry))
            return false;

        if (entry.ExpiresAt <= timeProvider.GetUtcNow())
        {
            _memo.TryRemove(redisKey, out _);
            return false;
        }

        token = entry.Token;
        return true;
    }

    private void WriteMemo(string redisKey, string token)
    {
        var memoSeconds = options.Value.GenerationMemoSeconds;
        if (memoSeconds <= 0)
            return;

        _memo[redisKey] = new MemoizedToken(token, timeProvider.GetUtcNow().AddSeconds(memoSeconds));
    }

    private static string CreateGenerationKey(string componentTypeKey, string domain, string key)
        => $"{componentTypeKey}:{domain}:{key}:gen";

    private readonly record struct MemoizedToken(string Token, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Cache payload wrapper. The token is stored as a property rather than a bare string so the entry
    /// round-trips through the cache serializer the same way every other cached value does.
    /// </summary>
    /// <param name="Token">The generation token.</param>
    public sealed record ComponentGenerationEntry(string Token);
}
