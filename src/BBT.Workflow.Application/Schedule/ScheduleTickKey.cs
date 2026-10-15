using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Schedule;

/// <summary>
/// Derives the instance key for a scheduled start from the tick that caused it.
/// <para>
/// This is what makes a scheduled start safe on more than one replica. Dapr's cron binding has no
/// leader election: every sidecar that loads the component runs its own ticker, so an N-replica
/// deployment calls the schedule endpoint N times for the same tick. Giving each of those calls the
/// SAME instance key lets the start path's existing key idempotency collapse them into one instance
/// (<c>InstanceCommandAppService.CheckExistingInstanceAsync</c> returns the active instance instead
/// of creating a second).
/// </para>
/// <para>
/// The key is <c>{scheduleId}-{yyyyMMddTHHmmssZ}</c>. Two properties matter:
/// </para>
/// <list type="bullet">
/// <item><description><b>Truncated to the second.</b> Replicas do not observe the same instant —
/// each sidecar stamps <c>readTimeUTC</c> from its own clock as the tick fires, so the values differ
/// in the sub-second digits. Truncating is what makes them agree. It also sidesteps Go's variable
/// fractional format (<c>time.Time.String()</c> trims trailing zeros, so the digit count is not
/// fixed).</description></item>
/// <item><description><b>Scoped by schedule.</b> Two components may target the same workflow on
/// overlapping schedules and must still produce separate instances when they coincide. The caller
/// passes a distinct <c>scheduleId</c> per component to keep them apart; without one the workflow
/// key is used, which deliberately collapses coincident ticks of the same flow.</description></item>
/// </list>
/// </summary>
public static class ScheduleTickKey
{
    /// <summary>
    /// Header carrying the tick instant. Dapr's cron binding sets it from
    /// <c>time.Now().UTC().String()</c>, e.g. <c>2026-10-05 07:25:42.93172757 +0000 UTC</c>.
    /// Lower-cased because the endpoint normalises header names.
    /// </summary>
    public const string ReadTimeUtcHeader = "readtimeutc";

    /// <summary>The leading <c>yyyy-MM-dd HH:mm:ss</c> of the Go timestamp — everything we keep.</summary>
    private const int SecondPrecisionLength = 19;

    private const string GoSecondFormat = "yyyy-MM-dd HH:mm:ss";
    private const string KeyInstantFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>Rendered instant plus its separator: <c>-yyyyMMddTHHmmssZ</c>.</summary>
    private const int SuffixLength = 17;

    /// <summary>
    /// How much of <see cref="InstanceConstants.MaxKeyLength"/> the schedule scope may occupy. The
    /// column is not advisory: a longer key fails the insert, and because nothing retries a cron tick
    /// the schedule would simply stop producing instances.
    /// </summary>
    private const int MaxScopeLength = InstanceConstants.MaxKeyLength - SuffixLength;

    /// <summary>Hex characters of digest appended when a scope has to be shortened.</summary>
    private const int DigestLength = 8;

    /// <summary>
    /// Builds the instance key for one tick.
    /// </summary>
    /// <param name="workflow">Workflow key; the fallback schedule scope when none is supplied.</param>
    /// <param name="scheduleId">
    /// Caller-supplied identity of the schedule, so overlapping components stay distinct. Optional.
    /// </param>
    /// <param name="readTimeUtcHeader">Raw <c>readtimeutc</c> header value, if the caller sent one.</param>
    /// <param name="nowUtc">
    /// Clock reading used when the header is absent or unparsable — a non-Dapr caller, or a future
    /// Dapr version that changes the format. Same truncation applies, so concurrent calls in the same
    /// second still collapse.
    /// </param>
    public static string Derive(string workflow, string? scheduleId, string? readTimeUtcHeader, DateTime nowUtc)
    {
        var scope = string.IsNullOrWhiteSpace(scheduleId) ? workflow : scheduleId.Trim();
        var instant = ParseTick(readTimeUtcHeader) ?? nowUtc;

        scope = Fit(scope);

        return $"{scope}-{instant.ToString(KeyInstantFormat, CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// True when the header carried a tick this code understands. The endpoint logs the fallback,
    /// because a missing tick silently weakens the cross-replica guarantee to "same second" rather
    /// than "same tick".
    /// </summary>
    public static bool TryParseTick(string? readTimeUtcHeader, out DateTime tickUtc)
    {
        var parsed = ParseTick(readTimeUtcHeader);
        tickUtc = parsed ?? default;
        return parsed.HasValue;
    }

    /// <summary>
    /// Reads the leading second-precision portion of Go's <c>time.Time.String()</c> output. The
    /// remainder (fractional digits, numeric offset, zone name) is deliberately ignored: Dapr always
    /// stamps this value in UTC, and the sub-second part is exactly what must not reach the key.
    /// </summary>
    private static DateTime? ParseTick(string? readTimeUtcHeader)
    {
        if (string.IsNullOrWhiteSpace(readTimeUtcHeader))
            return null;

        var value = readTimeUtcHeader.Trim();
        if (value.Length < SecondPrecisionLength)
            return null;

        return DateTime.TryParseExact(
            value[..SecondPrecisionLength],
            GoSecondFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Keeps the scope inside the key column. A workflow key may itself be
    /// <see cref="InstanceConstants.MaxKeyLength"/> characters, so appending the instant can overflow
    /// on its own — before anyone supplies a long <c>scheduleId</c>.
    /// <para>
    /// An over-long scope is truncated and given a digest of the ORIGINAL value, so two different long
    /// scopes that share a prefix still produce different keys. The digest is SHA-256 rather than
    /// <see cref="string.GetHashCode()"/> precisely because this value must be identical in every
    /// replica: .NET randomises string hash codes per process, which would hand each pod a different
    /// key and defeat the deduplication this type exists for.
    /// </para>
    /// </summary>
    private static string Fit(string scope)
    {
        if (scope.Length <= MaxScopeLength)
            return scope;

        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..DigestLength].ToLowerInvariant();

        return string.Concat(scope.AsSpan(0, MaxScopeLength - DigestLength - 1), "-", digest);
    }
}
