using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Declarative long-poll interaction directive for a state.
/// When <see cref="Terminate"/> is true, the transition pipeline pauses after the
/// state's OnEntry tasks run and the State (long-poll) function tells the client to
/// stop polling, render the entered-state screen, and acknowledge — at which point
/// the pipeline resumes (Schedule → Auto → Finish → Finalize). A fallback schedule
/// (see <see cref="FallbackTimeoutSeconds"/>) auto-resumes the pipeline if the client
/// never acknowledges.
/// </summary>
public sealed class LongPollInteraction
{
    private LongPollInteraction()
    {
    }

    [JsonConstructor]
    private LongPollInteraction(
        bool terminate,
        int? fallbackTimeoutSeconds,
        List<RoleGrant>? roles)
    {
        Terminate = terminate;
        FallbackTimeoutSeconds = fallbackTimeoutSeconds;
        this.roles = roles ?? [];
    }

    /// <summary>
    /// When true, entering the state terminates the client's active long-poll request.
    /// </summary>
    public bool Terminate { get; private set; }

    /// <summary>
    /// Acknowledge fallback window in seconds. If the client does not acknowledge within
    /// this window, a scheduled job resumes the pipeline. Defaults to 60 when null.
    /// </summary>
    public int? FallbackTimeoutSeconds { get; private set; }

    [JsonInclude]
    [JsonPropertyName("roles")]
    private List<RoleGrant> roles = new();

    /// <summary>
    /// Role grants controlling which callers receive the long-poll termination signal.
    /// Empty means default-allow (every caller is signalled). Evaluated with the
    /// standard DENY-wins / allowlist semantics used elsewhere for role grants.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyCollection<RoleGrant> Roles => roles.AsReadOnly();
}
