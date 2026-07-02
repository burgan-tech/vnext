using System.Text;
using BBT.Workflow.Execution.LongPoll;

namespace BBT.Workflow.Instances;

/// <summary>
/// Structured, resolvable identity for a Dapr background job.
/// </summary>
/// <remarks>
/// <para>
/// Wire format: <c>vnext.job.v1.{type}.{instanceId}[.{sourceState}].{key}</c>
/// </para>
/// <list type="bullet">
///   <item>Delimiter is <c>.</c> — accepted by Dapr's Jobs API (the name becomes the
///   <c>/job/{name}</c> callback route). Every field uses only <c>[A-Za-z0-9_-]</c>, so the whole
///   name stays within the Dapr-safe alphabet with no escaping/marker characters.</item>
///   <item><c>vnext.job.v1</c> is a fixed namespace + format version so the parser can reject
///   foreign names deterministically and the scheme can evolve.</item>
///   <item><c>{type}</c> is a short controlled-vocabulary code (never free text) — see
///   <see cref="JobType"/>. This distinguishes async vs scheduled transitions, which shared an
///   identical name before this scheme.</item>
///   <item><c>{instanceId}</c> is a GUID in "N" format (32 hex, no dashes).</item>
///   <item><c>{sourceState}</c> (transition jobs only) is the state the transition fires from. It
///   scopes the name so two transitions that share a key across different states do not collide
///   into one Dapr job. Omitted for long-poll-ack / state-notify, and for legacy names written
///   before source-state scoping (which parse with <see cref="SourceState"/> = <c>null</c>).</item>
///   <item><c>{key}</c> is the final field: the transition key for transition jobs, the well-known
///   key for long-poll-ack, the state key for state-notify; absent for timeout.</item>
/// </list>
/// <para>
/// State and transition keys are required to be within <c>[A-Za-z0-9_-]</c> (no <c>.</c>). A key
/// with any other character would previously produce a name Dapr rejects; the builder now fails
/// fast with a clear error instead.
/// </para>
/// </remarks>
public sealed record JobName
{
    private const string Prefix = "vnext.job.v1";
    private const char Delimiter = '.';

    private JobName(JobType type, Guid instanceId, string? sourceState, string? transitionKey, string value)
    {
        Type = type;
        InstanceId = instanceId;
        SourceState = sourceState;
        TransitionKey = transitionKey;
        Value = value;
    }

    /// <summary>The job kind decoded from the name.</summary>
    public JobType Type { get; }

    /// <summary>The owning workflow instance id.</summary>
    public Guid InstanceId { get; }

    /// <summary>
    /// The source-state key the transition fires from. <c>null</c> for jobs without source-state
    /// scoping (timeout, long-poll-ack, state-notify) and for legacy names. Disambiguates two
    /// transitions that share a key across different states in one instance.
    /// </summary>
    public string? SourceState { get; }

    /// <summary>
    /// The transition key (or well-known job key / state key) this job targets. <c>null</c> for
    /// timeout jobs.
    /// </summary>
    public string? TransitionKey { get; }

    /// <summary>Back-compatible alias for the final key field. Prefer <see cref="TransitionKey"/>.</summary>
    public string? Segment => TransitionKey;

    /// <summary>The wire string persisted as the Dapr job name and on the instance-job row.</summary>
    public string Value { get; }

    /// <summary>
    /// Builds the name of an async transition continuation job, scoped by the source state so that
    /// two transitions sharing a key across different states never collide into one Dapr job.
    /// </summary>
    public static JobName ForAsyncTransition(Guid instanceId, string sourceState, string transitionKey)
        => BuildScopedTransition(JobType.AsyncTransition, instanceId, sourceState, transitionKey);

    /// <summary>
    /// Builds the name of a timer-based scheduled transition job, scoped by the source state (see
    /// <see cref="ForAsyncTransition"/> for the collision rationale).
    /// </summary>
    public static JobName ForScheduledTransition(Guid instanceId, string sourceState, string transitionKey)
        => BuildScopedTransition(JobType.ScheduledTransition, instanceId, sourceState, transitionKey);

    /// <summary>Builds the name of a workflow timeout job.</summary>
    public static JobName ForTimeout(Guid instanceId)
        => Build(JobType.Timeout, instanceId, sourceState: null, transitionKey: null);

    /// <summary>Builds the name of a long-poll acknowledge fallback job.</summary>
    public static JobName ForLongPollAck(Guid instanceId)
        => Build(JobType.LongPollAck, instanceId, sourceState: null,
            transitionKey: ValidateKey(LongPollAckConstants.JobKey, nameof(LongPollAckConstants.JobKey)));

    /// <summary>Builds the name of a state-level notification dispatch job.</summary>
    public static JobName ForStateNotify(Guid instanceId, string stateKey)
        => Build(JobType.StateNotify, instanceId, sourceState: null,
            transitionKey: ValidateKey(stateKey, nameof(stateKey)));

    /// <summary>
    /// Attempts to parse a structured job name. Returns <c>false</c> for any value that does not
    /// match the <c>vnext.job.v1</c> scheme (e.g. foreign names).
    /// </summary>
    public static bool TryParse(string? value, out JobName result)
    {
        result = null!;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var expectedPrefix = Prefix + Delimiter; // "vnext.job.v1."
        if (!value.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // {type}.{instanceId}[.{sourceState}].{key} — every field is a plain, delimiter-free token,
        // so a full split is unambiguous.
        var parts = value[expectedPrefix.Length..].Split(Delimiter);
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

        var trailing = parts.Length - 2; // number of key fields after type + instanceId
        string? sourceState = null;
        string? transitionKey = null;

        switch (type)
        {
            case JobType.Timeout:
                if (trailing != 0)
                {
                    return false;
                }
                break;

            case JobType.AsyncTransition:
            case JobType.ScheduledTransition:
                // Current: {sourceState}.{key}. Legacy (pre source-state scoping): {key} only.
                if (trailing == 2)
                {
                    sourceState = parts[2];
                    transitionKey = parts[3];
                }
                else if (trailing == 1)
                {
                    transitionKey = parts[2];
                }
                else
                {
                    return false;
                }
                break;

            case JobType.LongPollAck:
            case JobType.StateNotify:
                if (trailing != 1)
                {
                    return false;
                }
                transitionKey = parts[2];
                break;

            default:
                return false;
        }

        result = new JobName(type, instanceId, sourceState, transitionKey, value);
        return true;
    }

    /// <summary>Parses a structured job name or throws when the format is invalid.</summary>
    public static JobName Parse(string value)
        => TryParse(value, out var result)
            ? result
            : throw new FormatException($"Invalid job name format: '{value}'.");

    /// <inheritdoc />
    public override string ToString() => Value;

    private static JobName BuildScopedTransition(
        JobType type, Guid instanceId, string sourceState, string transitionKey)
    {
        ValidateKey(transitionKey, nameof(transitionKey));

        // No source state available (rare edge paths) → emit a legacy-shaped single-key name.
        var source = string.IsNullOrEmpty(sourceState) ? null : ValidateKey(sourceState, nameof(sourceState));
        return Build(type, instanceId, source, transitionKey);
    }

    private static JobName Build(JobType type, Guid instanceId, string? sourceState, string? transitionKey)
    {
        var builder = new StringBuilder(Prefix)
            .Append(Delimiter).Append(ToWireCode(type))
            .Append(Delimiter).Append(instanceId.ToString("N"));

        if (sourceState is not null)
        {
            builder.Append(Delimiter).Append(sourceState);
        }

        if (transitionKey is not null)
        {
            builder.Append(Delimiter).Append(transitionKey);
        }

        var value = builder.ToString();
        if (value.Length > InstanceJobConstants.MaxJobNameLength)
        {
            throw new ArgumentException(
                $"Job name length {value.Length} exceeds the maximum of {InstanceJobConstants.MaxJobNameLength}.",
                nameof(transitionKey));
        }

        return new JobName(type, instanceId, sourceState, transitionKey, value);
    }

    /// <summary>
    /// Ensures a state/transition key is a non-empty, Dapr-safe token (<c>[A-Za-z0-9_-]</c>).
    /// Throws <see cref="ArgumentException"/> otherwise — such a key cannot form a valid Dapr job name.
    /// </summary>
    private static string ValidateKey(string key, string paramName)
    {
        if (string.IsNullOrEmpty(key) || !IsSafe(key))
        {
            throw new ArgumentException(
                $"State/transition key '{key}' must be non-empty and contain only [A-Za-z0-9_-] " +
                "to form a valid Dapr job name.",
                paramName);
        }

        return key;
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

    private static bool IsSafe(string value)
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
