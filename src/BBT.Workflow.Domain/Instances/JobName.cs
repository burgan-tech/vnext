using System.Text;
using BBT.Workflow.Execution.LongPoll;

namespace BBT.Workflow.Instances;

/// <summary>
/// Structured, resolvable identity for a Dapr background job.
/// </summary>
/// <remarks>
/// <para>
/// Wire format: <c>vnext.job.v1.{type}.{instanceId}[.{segment}]</c>
/// </para>
/// <list type="bullet">
///   <item>Delimiter is <c>.</c> — unreserved in URL path segments (RFC 3986), so it survives
///   Dapr's scheduler / etcd-backed store unmodified. The classic URN <c>:</c> delimiter is
///   intentionally avoided because Dapr job names are used in URL paths.</item>
///   <item><c>vnext.job.v1</c> is a fixed namespace + format version so the parser can reject
///   foreign / legacy names deterministically and the scheme can evolve.</item>
///   <item><c>{type}</c> is a short controlled-vocabulary code (never free text) — see
///   <see cref="JobType"/>. This is what distinguishes async vs scheduled transitions, which
///   shared an identical name before this scheme.</item>
///   <item><c>{instanceId}</c> is a GUID in "N" format (32 hex, no dashes).</item>
///   <item><c>{segment}</c> is the optional, type-specific final segment (the transition key for
///   transition jobs, the well-known key for long-poll-ack, absent for timeout). As the reserved
///   final segment it is the only part allowed to carry arbitrary user input; it is Base64Url
///   encoded (with a <c>~</c> marker) whenever it contains a character outside
///   <c>[A-Za-z0-9_-]</c>, guaranteeing collision-free, reversible round-trips.</item>
/// </list>
/// </remarks>
public sealed record JobName
{
    private const string Prefix = "vnext.job.v1";
    private const char Delimiter = '.';
    private const char EncodedMarker = '~';

    private JobName(JobType type, Guid instanceId, string? segment, string value)
    {
        Type = type;
        InstanceId = instanceId;
        Segment = segment;
        Value = value;
    }

    /// <summary>The job kind decoded from the name.</summary>
    public JobType Type { get; }

    /// <summary>The owning workflow instance id.</summary>
    public Guid InstanceId { get; }

    /// <summary>
    /// The decoded final segment: the transition key for transition jobs, the well-known job key
    /// for long-poll-ack, or <c>null</c> for timeout jobs.
    /// </summary>
    public string? Segment { get; }

    /// <summary>The encoded wire string persisted as the Dapr job name and on the instance-job row.</summary>
    public string Value { get; }

    /// <summary>Builds the name of an async transition continuation job.</summary>
    public static JobName ForAsyncTransition(Guid instanceId, string transitionKey)
        => Build(JobType.AsyncTransition, instanceId, transitionKey);

    /// <summary>Builds the name of a timer-based scheduled transition job.</summary>
    public static JobName ForScheduledTransition(Guid instanceId, string transitionKey)
        => Build(JobType.ScheduledTransition, instanceId, transitionKey);

    /// <summary>Builds the name of a workflow timeout job.</summary>
    public static JobName ForTimeout(Guid instanceId)
        => Build(JobType.Timeout, instanceId, segment: null);

    /// <summary>Builds the name of a long-poll acknowledge fallback job.</summary>
    public static JobName ForLongPollAck(Guid instanceId)
        => Build(JobType.LongPollAck, instanceId, LongPollAckConstants.JobKey);

    /// <summary>Builds the name of a state-level notification dispatch job.</summary>
    public static JobName ForStateNotify(Guid instanceId, string stateKey)
        => Build(JobType.StateNotify, instanceId, stateKey);

    /// <summary>
    /// Attempts to parse a structured job name. Returns <c>false</c> for any value that does not
    /// match the <c>vnext.job.v1</c> scheme (e.g. legacy names written before the rollout).
    /// </summary>
    public static bool TryParse(string? value, out JobName result)
    {
        result = null!;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string expectedPrefix = Prefix + Delimiter; // "vnext.job.v1."
        if (!value.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Remaining: {type}.{instanceId}[.{segment}] — segment never contains the delimiter on
        // the wire (it is encoded when it would), but split with a bound for defensiveness.
        var parts = value[expectedPrefix.Length..].Split(Delimiter, 3);
        if (parts.Length < 2)
        {
            return false;
        }

        var type = FromWireCode(parts[0]);
        if (type == JobType.Unknown)
        {
            return false;
        }

        if (!Guid.TryParseExact(parts[1], "N", out var instanceId))
        {
            return false;
        }

        var segment = parts.Length == 3 ? Decode(parts[2]) : null;
        result = new JobName(type, instanceId, segment, value);
        return true;
    }

    /// <summary>Parses a structured job name or throws when the format is invalid.</summary>
    public static JobName Parse(string value)
        => TryParse(value, out var result)
            ? result
            : throw new FormatException($"Invalid job name format: '{value}'.");

    /// <inheritdoc />
    public override string ToString() => Value;

    private static JobName Build(JobType type, Guid instanceId, string? segment)
    {
        var builder = new StringBuilder(Prefix)
            .Append(Delimiter).Append(ToWireCode(type))
            .Append(Delimiter).Append(instanceId.ToString("N"));

        var decodedSegment = string.IsNullOrEmpty(segment) ? null : segment;
        if (decodedSegment is not null)
        {
            builder.Append(Delimiter).Append(Encode(decodedSegment));
        }

        var value = builder.ToString();
        if (value.Length > InstanceJobConstants.MaxJobNameLength)
        {
            throw new ArgumentException(
                $"Job name length {value.Length} exceeds the maximum of {InstanceJobConstants.MaxJobNameLength}.",
                nameof(segment));
        }

        return new JobName(type, instanceId, decodedSegment, value);
    }

    private static string ToWireCode(JobType type) => type switch
    {
        JobType.AsyncTransition => "tx",
        JobType.ScheduledTransition => "sx",
        JobType.Timeout => "to",
        JobType.LongPollAck => "la",
        JobType.StateNotify => "sn",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported job type.")
    };

    private static JobType FromWireCode(string code) => code switch
    {
        "tx" => JobType.AsyncTransition,
        "sx" => JobType.ScheduledTransition,
        "to" => JobType.Timeout,
        "la" => JobType.LongPollAck,
        "sn" => JobType.StateNotify,
        _ => JobType.Unknown
    };

    private static string Encode(string raw)
    {
        if (IsUrlSafe(raw))
        {
            return raw;
        }

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return EncodedMarker + base64;
    }

    private static string Decode(string segment)
    {
        if (segment.Length == 0 || segment[0] != EncodedMarker)
        {
            return segment;
        }

        var base64 = segment[1..].Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private static bool IsUrlSafe(string value)
    {
        foreach (var c in value)
        {
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
