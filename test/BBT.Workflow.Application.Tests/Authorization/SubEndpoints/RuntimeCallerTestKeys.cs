using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.Runtime;
using BBT.Workflow.RuntimeCaller;
using NSubstitute;

namespace BBT.Workflow.Authorization.SubEndpoints;

/// <summary>Key rings, clocks and identities for runtime-caller token tests (vnext#1265).</summary>
public static class RuntimeCallerTestKeys
{
    /// <summary>A fixed instant all tests sign and verify at unless they move the clock.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A deterministic 32-byte key derived from <paramref name="seed"/>.</summary>
    public static byte[] Key(byte seed) => Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();

    /// <summary>The key-ring file content for <paramref name="keys"/>, the first one active.</summary>
    public static string RingJson(params (string Id, byte[] Key)[] keys)
        => JsonSerializer.Serialize(new
        {
            activeKeyId = keys[0].Id,
            keys = keys.ToDictionary(k => k.Id, k => Convert.ToBase64String(k.Key))
        });

    /// <summary>A parsed ring for <paramref name="keys"/>, the first one active.</summary>
    public static RuntimeCallerKeyRing Ring(params (string Id, byte[] Key)[] keys)
        => RuntimeCallerKeyRing.Parse(RingJson(keys));

    /// <summary>The usual single-key ring.</summary>
    public static RuntimeCallerKeyRing DefaultRing() => Ring(("k1", Key(1)));

    /// <summary>An identity over <paramref name="ring"/> for <paramref name="domain"/> at <paramref name="clock"/>.</summary>
    public static RuntimeCallerIdentity Identity(
        RuntimeCallerKeyRing? ring,
        string domain = "core",
        TimeProvider? clock = null)
    {
        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.Domain.Returns(domain);
        return new RuntimeCallerIdentity(ring, runtimeInfo, clock ?? new FixedTimeProvider(Now));
    }

    /// <summary>A clock that stays where it is set.</summary>
    public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        /// <summary>The current instant.</summary>
        public DateTimeOffset Value { get; set; } = now;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => Value;
    }
}
