using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BBT.Workflow.Execution.RuntimeCaller;

/// <summary>
/// The short-lived HMAC token a runtime attaches to its own calls to a peer's runtime-internal
/// endpoints, so the peer can tell those calls from a client's (vnext#1265).
/// <para>
/// Wire form: <c>v1;k={keyId};t={unixSeconds};d={callerDomain};c={callerClass};s={base64url(mac)}</c>.
/// The MAC is HMAC-SHA256 over
/// <c>v1 \n keyId \n t \n callerDomain \n callerClass \n operation \n domain \n workflow \n instance</c>,
/// the last five lower-cased invariant and <c>-</c> for a missing instance.
/// </para>
/// <para>
/// A token is minted per send attempt and never stored. It is bound to one operation on one route
/// (domain, workflow and, for completion, the parent instance) and expires after the clock-skew
/// window, so one leaked at an older, ungated callee is worth nothing elsewhere.
/// </para>
/// </summary>
public static class RuntimeCallerToken
{
    /// <summary>The request header the token travels in.</summary>
    public const string HeaderName = "x-vnext-runtime-token";

    /// <summary>The format version, first field of the wire form and of the signed string.</summary>
    public const string FormatVersion = "v1";

    /// <summary>Default accepted distance between the token's timestamp and the receiver's clock.</summary>
    public const int DefaultMaxClockSkewSeconds = 300;

    private const int MacBytes = 32;
    private const int MaxTokenLength = 512;

    /// <summary>Mints a token for <paramref name="target"/> with <paramref name="key"/>.</summary>
    public static string Create(
        string keyId,
        ReadOnlySpan<byte> key,
        RuntimeCallTarget target,
        string callerDomain,
        string callerClass,
        DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeSeconds();
        var mac = ComputeMac(key, keyId, timestamp, callerDomain, callerClass, target);
        return string.Create(CultureInfo.InvariantCulture,
            $"{FormatVersion};k={keyId};t={timestamp};d={Uri.EscapeDataString(callerDomain)};" +
            $"c={Uri.EscapeDataString(callerClass)};s={Base64Url.EncodeToString(mac)}");
    }

    /// <summary>
    /// Verifies <paramref name="value"/> for <paramref name="target"/>. The signature is checked in
    /// constant time and before the timestamp, so a forged token reports <see cref="RuntimeCallerTokenStatus.BadSignature"/>
    /// and only a genuine one can report <see cref="RuntimeCallerTokenStatus.ClockSkew"/>.
    /// </summary>
    public static RuntimeCallerTokenVerdict Verify(
        string? value,
        RuntimeCallerKeyRing ring,
        RuntimeCallTarget target,
        DateTimeOffset now,
        int maxClockSkewSeconds)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new RuntimeCallerTokenVerdict(RuntimeCallerTokenStatus.Missing);

        if (!TryParse(value, out var parsed))
            return new RuntimeCallerTokenVerdict(RuntimeCallerTokenStatus.Malformed);

        // Until the MAC verifies, nothing in the token is the runtime's word: report no claims (a client
        // could put any text there), only a key id that is well formed — a ring key's id is, and a
        // well-formed unknown id names the peer key to look for.
        if (!ring.TryGetKey(parsed.KeyId, out var key))
            return new RuntimeCallerTokenVerdict(RuntimeCallerTokenStatus.UnknownKey,
                RuntimeCallerKeyRing.IsWellFormedKeyId(parsed.KeyId) ? parsed.KeyId : null);

        var expected = ComputeMac(key, parsed.KeyId, parsed.Timestamp, parsed.CallerDomain, parsed.CallerClass, target);
        if (!CryptographicOperations.FixedTimeEquals(expected, parsed.Mac))
            return new RuntimeCallerTokenVerdict(RuntimeCallerTokenStatus.BadSignature, parsed.KeyId);

        var skew = now.ToUnixTimeSeconds() - parsed.Timestamp;
        return Math.Abs(skew) > maxClockSkewSeconds
            ? parsed.Verdict(RuntimeCallerTokenStatus.ClockSkew, skew)
            : parsed.Verdict(RuntimeCallerTokenStatus.Valid, skew);
    }

    private static byte[] ComputeMac(
        ReadOnlySpan<byte> key,
        string keyId,
        long timestamp,
        string callerDomain,
        string callerClass,
        RuntimeCallTarget target)
    {
        var canonical = string.Join('\n',
            FormatVersion,
            keyId,
            timestamp.ToString(CultureInfo.InvariantCulture),
            callerDomain.ToLowerInvariant(),
            callerClass.ToLowerInvariant(),
            target.Operation.ToLowerInvariant(),
            target.Domain.ToLowerInvariant(),
            target.Workflow.ToLowerInvariant(),
            string.IsNullOrEmpty(target.Instance) ? "-" : target.Instance.ToLowerInvariant());
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));
    }

    private static bool TryParse(string value, out ParsedToken parsed)
    {
        parsed = default;
        if (value.Length > MaxTokenLength)
            return false;

        var fields = value.Split(';');
        if (fields.Length != 6 || !string.Equals(fields[0], FormatVersion, StringComparison.Ordinal))
            return false;

        if (!TryField(fields[1], "k=", out var keyId)
            || !TryField(fields[2], "t=", out var timestampText)
            || !TryField(fields[3], "d=", out var callerDomain)
            || !TryField(fields[4], "c=", out var callerClass)
            || !TryField(fields[5], "s=", out var macText))
            return false;

        if (!long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
            return false;

        byte[] mac;
        try
        {
            mac = Base64Url.DecodeFromChars(macText);
        }
        catch (FormatException)
        {
            return false;
        }

        if (mac.Length != MacBytes)
            return false;

        parsed = new ParsedToken(keyId, timestamp, Uri.UnescapeDataString(callerDomain),
            Uri.UnescapeDataString(callerClass), mac);
        return true;
    }

    private static bool TryField(string field, string prefix, out string value)
    {
        value = string.Empty;
        if (!field.StartsWith(prefix, StringComparison.Ordinal) || field.Length == prefix.Length)
            return false;

        value = field[prefix.Length..];
        return true;
    }

    private readonly record struct ParsedToken(
        string KeyId,
        long Timestamp,
        string CallerDomain,
        string CallerClass,
        byte[] Mac)
    {
        public RuntimeCallerTokenVerdict Verdict(RuntimeCallerTokenStatus status, long? skewSeconds)
            => new(status, KeyId, CallerDomain, CallerClass, skewSeconds);
    }
}

/// <summary>Outcome of verifying a runtime-caller token.</summary>
public enum RuntimeCallerTokenStatus
{
    /// <summary>No token was sent.</summary>
    Missing,

    /// <summary>A token was sent but is not in the wire form.</summary>
    Malformed,

    /// <summary>The token names a key id this ring does not hold.</summary>
    UnknownKey,

    /// <summary>The MAC does not match: a different key, a different route or operation, or a forgery.</summary>
    BadSignature,

    /// <summary>A genuine token whose timestamp is outside the accepted window.</summary>
    ClockSkew,

    /// <summary>A genuine, current token for this route.</summary>
    Valid
}

/// <summary>
/// The verdict on one token. <see cref="CallerDomain"/>, <see cref="CallerClass"/> and
/// <see cref="SkewSeconds"/> are set only once the signature verified (<see cref="RuntimeCallerTokenStatus.Valid"/>
/// or <see cref="RuntimeCallerTokenStatus.ClockSkew"/>); even then they are attribution, signed by some
/// key holder, never authorization. <see cref="KeyId"/> is set for a well-formed key id.
/// </summary>
/// <param name="Status">The outcome.</param>
/// <param name="KeyId">The key id the token named, when it is well formed.</param>
/// <param name="CallerDomain">The domain the token was minted for, once the signature verified.</param>
/// <param name="CallerClass">The runtime path that minted the token, once the signature verified.</param>
/// <param name="SkewSeconds">Receiver clock minus token timestamp, for a genuine token.</param>
public sealed record RuntimeCallerTokenVerdict(
    RuntimeCallerTokenStatus Status,
    string? KeyId = null,
    string? CallerDomain = null,
    string? CallerClass = null,
    long? SkewSeconds = null)
{
    /// <summary>Whether the token proves a runtime call for this route.</summary>
    public bool IsValid => Status == RuntimeCallerTokenStatus.Valid;

    /// <summary>The snake_case reason used in logs and span tags.</summary>
    public string Reason => Status switch
    {
        RuntimeCallerTokenStatus.Missing => "missing",
        RuntimeCallerTokenStatus.Malformed => "malformed",
        RuntimeCallerTokenStatus.UnknownKey => "unknown_key",
        RuntimeCallerTokenStatus.BadSignature => "bad_signature",
        RuntimeCallerTokenStatus.ClockSkew => "clock_skew",
        _ => "valid"
    };
}
