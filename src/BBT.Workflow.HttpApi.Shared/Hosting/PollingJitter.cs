namespace BBT.Workflow.Hosting;

/// <summary>
/// Adds randomized jitter to polling delays so horizontally-scaled workers (multiple inbox/outbox
/// replicas, or several chain-reaper instances) do not poll their shared queue tables in lockstep.
/// Lockstep polling produces periodic thundering-herd load and lock contention on the
/// <c>sys_queues</c> tables; spreading the replicas out smooths the load. Uses
/// <see cref="System.Random.Shared"/>, which is thread-safe.
/// </summary>
public static class PollingJitter
{
    /// <summary>
    /// Returns <paramref name="interval"/> plus a random fraction in <c>[0, maxFraction]</c> of it,
    /// so each replica's loop drifts and the replicas stay out of phase over time. Applied to the
    /// per-cycle delay.
    /// </summary>
    /// <param name="interval">The base polling interval.</param>
    /// <param name="maxFraction">Maximum jitter as a fraction of the interval (default 25%).</param>
    public static TimeSpan Apply(TimeSpan interval, double maxFraction = 0.25)
    {
        if (interval <= TimeSpan.Zero || maxFraction <= 0)
            return interval;

        var maxJitterMs = (int)(interval.TotalMilliseconds * maxFraction);
        if (maxJitterMs <= 0)
            return interval;

        return interval + TimeSpan.FromMilliseconds(Random.Shared.Next(0, maxJitterMs + 1));
    }

    /// <summary>
    /// Returns <paramref name="baseDelay"/> plus a random offset in <c>[0, window]</c>, so replicas
    /// begin their poll loops at different phases after start-up.
    /// </summary>
    /// <param name="baseDelay">The minimum warm-up delay before the first poll.</param>
    /// <param name="window">The random spread added on top of the base delay.</param>
    public static TimeSpan Startup(TimeSpan baseDelay, TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
            return baseDelay;

        return baseDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)window.TotalMilliseconds + 1));
    }
}
