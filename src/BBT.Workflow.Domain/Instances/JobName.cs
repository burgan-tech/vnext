using System.Text;
using BBT.Workflow.Execution.LongPoll;

namespace BBT.Workflow.Instances;

/// <summary>
/// Structured, resolvable identity for a Dapr background job.
/// </summary>
/// <remarks>
/// <para>
/// Wire format: <c>vnext.job.v1.{type}.{instanceId}[.{sourceState}].{key}[.{invocation}]</c>
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
///   <item><c>{key}</c> is the transition key for transition jobs, the well-known key for
///   long-poll-ack, the state key for state-notify; absent for timeout.</item>
///   <item><c>{invocation}</c> (transition jobs only) makes the name unique PER ENQUEUE. The
///   scheduler entry is keyed by name and is deleted by name once a one-shot job completes, so two
///   enqueues sharing a name are destructive: a <c>$self</c> automatic loop re-enqueues the same
///   (instance, sourceState, key) on every iteration, and the completing iteration would delete the
///   next one's trigger — the chain dies mid-loop and the instance stays Busy with no job to settle
///   it. It also keeps <c>MarkAsProcessedAsync</c> unambiguous. Logical identity (the "is a job for
///   this transition already active" guard) lives in the structured <c>InstanceJob</c> columns
///   instead — never in this string. Absent for timeout / long-poll-ack / state-notify (armed once
///   per instance or per state entry) and for names written before invocation scoping, which parse
///   with <see cref="Invocation"/> = <c>null</c>.</item>
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

    /// <summary>Hex characters of the job id kept as the per-enqueue invocation segment.</summary>
    private const int InvocationLength = 8;

    private JobName(
        JobType type,
        Guid instanceId,
        string? sourceState,
        string? transitionKey,
        string? invocation,
        string value)
    {
        Type = type;
        InstanceId = instanceId;
        SourceState = sourceState;
        TransitionKey = transitionKey;
        Invocation = invocation;
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

    /// <summary>
    /// The per-enqueue uniquifier of a transition job. <c>null</c> for job kinds that are armed once
    /// (timeout, long-poll-ack, state-notify) and for names written before invocation scoping.
    /// Carries no meaning beyond uniqueness — never match or look up a job by it.
    /// </summary>
    public string? Invocation { get; }

    /// <summary>Back-compatible alias for the final key field. Prefer <see cref="TransitionKey"/>.</summary>
    public string? Segment => TransitionKey;

    /// <summary>The wire string persisted as the Dapr job name and on the instance-job row.</summary>
    public string Value { get; }

    /// <summary>
    /// Builds the name of an async transition job (request accept or auto-chain continuation),
    /// scoped by the source state so two transitions sharing a key across different states never
    /// collide, and by <paramref name="invocationId"/> so two enqueues of the SAME transition never
    /// collide either (the scheduler entry is keyed by this name and deleted by name on completion).
    /// Pass the job's own id so the name and the durable <c>InstanceJob</c> row stay traceable.
    /// </summary>
    public static JobName ForAsyncTransition(
        Guid instanceId, string sourceState, string transitionKey, Guid invocationId)
        => BuildScopedTransition(
            JobType.AsyncTransition, instanceId, sourceState, transitionKey, invocationId);

    /// <summary>
    /// Builds the name of a timer-based scheduled transition job, scoped by the source state and the
    /// invocation (see <see cref="ForAsyncTransition"/> — a re-entered state re-arms the same timer).
    /// </summary>
    public static JobName ForScheduledTransition(
        Guid instanceId, string sourceState, string transitionKey, Guid invocationId)
        => BuildScopedTransition(
            JobType.ScheduledTransition, instanceId, sourceState, transitionKey, invocationId);

    /// <summary>Builds the name of a workflow timeout job.</summary>
    public static JobName ForTimeout(Guid instanceId)
        => Build(JobType.Timeout, instanceId, sourceState: null, transitionKey: null, invocation: null);

    /// <summary>Builds the name of a long-poll acknowledge fallback job.</summary>
    public static JobName ForLongPollAck(Guid instanceId)
        => Build(JobType.LongPollAck, instanceId, sourceState: null,
            transitionKey: ValidateKey(LongPollAckConstants.JobKey, nameof(LongPollAckConstants.JobKey)),
            invocation: null);

    /// <summary>Builds the name of a state-level notification dispatch job.</summary>
    public static JobName ForStateNotify(Guid instanceId, string stateKey)
        => Build(JobType.StateNotify, instanceId, sourceState: null,
            transitionKey: ValidateKey(stateKey, nameof(stateKey)), invocation: null);

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

        // {type}.{instanceId}[.{sourceState}].{key}[.{invocation}] — every field is a plain,
        // delimiter-free token, so counting the split segments is unambiguous.
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
        string? invocation = null;

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
                // Current: {sourceState}.{key}.{invocation}. Legacy: {sourceState}.{key} (pre
                // invocation scoping) or {key} only (pre source-state scoping).
                if (trailing == 3)
                {
                    sourceState = parts[2];
                    transitionKey = parts[3];
                    invocation = parts[4];
                }
                else if (trailing == 2)
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

        result = new JobName(type, instanceId, sourceState, transitionKey, invocation, value);
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
        JobType type, Guid instanceId, string sourceState, string transitionKey, Guid invocationId)
    {
        ValidateKey(transitionKey, nameof(transitionKey));

        // No source state available (rare edge paths) → emit a legacy-shaped single-key name.
        var source = string.IsNullOrEmpty(sourceState) ? null : ValidateKey(sourceState, nameof(sourceState));

        // The invocation segment only has to separate enqueues of the same transition on one
        // instance; the first 8 hex of the job id is short, Dapr-safe and traceable back to the row.
        // It is only appended when a source state is present: without one, `{key}.{invocation}` would
        // be indistinguishable from a source-state-scoped `{sourceState}.{key}` on the way back in.
        var invocation = source is null || invocationId == Guid.Empty
            ? null
            : invocationId.ToString("N")[..InvocationLength];

        return Build(type, instanceId, source, transitionKey, invocation);
    }

    private static JobName Build(
        JobType type, Guid instanceId, string? sourceState, string? transitionKey, string? invocation)
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

        if (invocation is not null)
        {
            builder.Append(Delimiter).Append(invocation);
        }

        var value = builder.ToString();
        if (value.Length > InstanceJobConstants.MaxJobNameLength)
        {
            throw new ArgumentException(
                $"Job name length {value.Length} exceeds the maximum of {InstanceJobConstants.MaxJobNameLength}.",
                nameof(transitionKey));
        }

        return new JobName(type, instanceId, sourceState, transitionKey, invocation, value);
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
