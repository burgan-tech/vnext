namespace BBT.Workflow.Execution.RuntimeCaller;

/// <summary>
/// Attaches the runtime-caller token to a runtime-generated call to a peer's runtime-internal
/// endpoint (vnext#1265). Called by the four runtime callers only — cross-domain SubFlow start,
/// cross-domain SubProcess start, the cross-domain completion relay and the Inbox completion
/// backup — never for a target a flow author chose (HttpTask, DaprService).
/// </summary>
public interface IRuntimeCallerSigner
{
    /// <summary>
    /// Removes any header named <see cref="RuntimeCallerToken.HeaderName"/> from
    /// <paramref name="request"/>, then, when a key ring is loaded, adds a fresh token for
    /// <paramref name="target"/>. Call it last, after every other header was merged, so neither an
    /// input nor a mapping header can replace or remove the token.
    /// </summary>
    void Stamp(HttpRequestMessage request, RuntimeCallTarget target, string callerClass);
}

/// <summary>
/// Verifies the runtime-caller token on a gated endpoint.
/// </summary>
public interface IRuntimeCallerVerifier
{
    /// <summary>Whether a key ring is loaded; without one no token can be valid.</summary>
    bool IsConfigured { get; }

    /// <summary>Verifies <paramref name="token"/> for <paramref name="target"/> against the loaded ring and clock.</summary>
    RuntimeCallerTokenVerdict Verify(string? token, RuntimeCallTarget target);
}

/// <summary>
/// The signer used when no runtime-caller identity is registered: it only removes a same-named
/// header, so the runtime never forwards a token it did not mint.
/// </summary>
public sealed class NullRuntimeCallerSigner : IRuntimeCallerSigner
{
    /// <summary>The shared instance.</summary>
    public static readonly NullRuntimeCallerSigner Instance = new();

    private NullRuntimeCallerSigner()
    {
    }

    /// <inheritdoc />
    public void Stamp(HttpRequestMessage request, RuntimeCallTarget target, string callerClass)
        => request.Headers.Remove(RuntimeCallerToken.HeaderName);
}
