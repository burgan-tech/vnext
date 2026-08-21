using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Caching;

/// <summary>
/// Wires a <see cref="CacheSet{T}"/> for <see cref="View"/> to in-memory doubles, using the real
/// <see cref="ComponentGenerationProvider"/> so generation semantics are exercised end to end rather
/// than stubbed out.
/// </summary>
public sealed class CacheSetTestHarness
{
    public const string TestDomain = "core";
    public const string TestKey = "account-type-selection-view";
    public const string ComponentType = "sys-views";

    public CacheSetTestHarness(Action<ComponentCacheOptions>? configure = null)
    {
        Time = new AdjustableTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        Cache = new FakeDistributedCacheService(Time);
        Backend = new FakeCacheBackend();
        Options = new ComponentCacheOptions();
        configure?.Invoke(Options);

        var optionsAccessor = new StaticOptions(Options);

        L1 = new ComponentL1Cache(optionsAccessor, Time, NullLogger<ComponentL1Cache>.Instance);

        Generations = new ComponentGenerationProvider(
            Cache,
            new SequentialGuidGenerator(),
            optionsAccessor,
            Time,
            NullLogger<ComponentGenerationProvider>.Instance);

        Sut = new CacheSet<View>(
            Cache,
            Backend,
            Generations,
            optionsAccessor,
            Time,
            NullLogger<CacheSet<View>>.Instance,
            L1);
    }

    public AdjustableTimeProvider Time { get; }
    public FakeDistributedCacheService Cache { get; }
    public FakeCacheBackend Backend { get; }
    public ComponentCacheOptions Options { get; }
    public ComponentL1Cache L1 { get; }
    public ComponentGenerationProvider Generations { get; }
    public CacheSet<View> Sut { get; }

    /// <summary>Builds a <see cref="View"/> carrying the given version.</summary>
    public static View CreateView(string version, string key = TestKey, string domain = TestDomain)
    {
        const string viewJson = """
        {
            "type": 1,
            "target": 1,
            "content": "{}"
        }
        """;

        var view = JsonSerializer.Deserialize<View>(viewJson, JsonSerializerConstants.JsonOptions)!;
        view.SetReference(new Reference(key, domain, ComponentType, version));
        return view;
    }

    /// <summary>Replaces the versions the backend reports as published.</summary>
    public void Publish(params string[] versions)
        => Backend.Versions = versions.Select(v => CreateView(v)).ToList();

    /// <summary>Adds a version to what the backend reports as published.</summary>
    public void AddPublished(string version)
        => Backend.Versions = [.. Backend.Versions, CreateView(version)];

    /// <summary>Removes a version from the backend, as a deactivation would.</summary>
    public void Deactivate(string version)
        => Backend.Versions = Backend.Versions
            .Where(v => !string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Reads the component's current generation token straight from the cache.</summary>
    public Task<string> CurrentGenerationAsync()
        => Generations.GetAsync(ComponentType, TestDomain, TestKey);

    public string FullKey(string version)
        => $"{ComponentType}:{TestDomain}:{TestKey}:full:{InstanceDataVersionComparer.CanonicalFullVersion(version)}";

    public string ResolutionKey(string generation, string spelling)
        => $"{ComponentType}:{TestDomain}:{TestKey}:res:{generation}:{spelling}";

    public string GenerationKey()
        => $"{ComponentType}:{TestDomain}:{TestKey}:gen";

    public string LegacyLatestKey()
        => $"{ComponentType}:{TestDomain}:{TestKey}:latest";

    public string LegacyArtifactKey(string spelling)
        => $"{ComponentType}:{TestDomain}:{TestKey}:artifact:{spelling}";

    /// <summary>Live resolution keys, with the generation segment stripped to the trailing spelling.</summary>
    public IReadOnlyList<string> ResolutionSpellingsInCache()
    {
        var prefix = $"{ComponentType}:{TestDomain}:{TestKey}:res:";
        return Cache.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..])
            .Select(rest => rest[(rest.IndexOf(':') + 1)..])
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// <see cref="ICacheBackend{T}"/> double reporting a settable version list and counting loads, so
    /// "this served zero database queries" can be asserted rather than assumed.
    /// </summary>
    public sealed class FakeCacheBackend : ICacheBackend<View>
    {
        private int _loadAllCallCount;

        public List<View> Versions { get; set; } = [];

        public int LoadAllCallCount => Volatile.Read(ref _loadAllCallCount);

        public int LoadCallCount { get; private set; }

        /// <summary>Blocks each load until released, to construct concurrent-miss scenarios.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>When true, loads throw — used to prove behaviour that must not depend on warming.</summary>
        public bool FailLoadAll { get; set; }

        public async Task<Result<List<View>>> LoadAllByKeyAsync(
            string domain,
            string key,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loadAllCallCount);

            if (FailLoadAll)
                throw new InvalidOperationException("Simulated backend load failure.");

            if (Gate is not null)
                await Gate.Task;

            return Result<List<View>>.Ok(Versions
                .Where(v => string.Equals(v.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))
                .ToList());
        }

        public Task<Result<View>> LoadAsync(
            string domain,
            string key,
            string? version,
            CancellationToken cancellationToken = default)
        {
            LoadCallCount++;

            var match = Versions.FirstOrDefault(v =>
                string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(match is null
                ? Result<View>.Fail(CacheErrors.ItemNotFoundInBackend<View>(domain, key, version))
                : Result<View>.Ok(match));
        }

        public void ResetCounts()
        {
            Interlocked.Exchange(ref _loadAllCallCount, 0);
            LoadCallCount = 0;
        }
    }

    /// <summary>A <see cref="TimeProvider"/> whose clock only moves when a test moves it.</summary>
    public sealed class AdjustableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class StaticOptions(ComponentCacheOptions value) : IOptions<ComponentCacheOptions>
    {
        public ComponentCacheOptions Value { get; } = value;
    }

    private sealed class SequentialGuidGenerator : IGuidGenerator
    {
        private int _counter;

        public Guid Create()
        {
            var next = Interlocked.Increment(ref _counter);
            return new Guid(next, 0, 0, new byte[8]);
        }
    }
}
