namespace BBT.Workflow.Authorization.Configuration;

/// <summary>
/// Selects and configures the runtime's caller-role provider. Bound once at startup from
/// <c>CallerRoleProvider</c> in appsettings; the choice is process-wide and never varies per request.
/// </summary>
public sealed class CallerRoleProviderOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "CallerRoleProvider";

    /// <summary>The default provider's identifier: <c>ICurrentUser.Roles</c> with the <c>role</c> header fallback.</summary>
    public const string DefaultProvider = "default";

    /// <summary>The morph-idm provider's identifier.</summary>
    public const string MorphIdmProvider = "morph-idm";

    /// <summary>
    /// Which provider resolves caller roles: <c>default</c> or <c>morph-idm</c>.
    /// Unrecognized values fall back to <c>default</c> rather than failing startup, so a typo degrades
    /// to the runtime's original behaviour instead of taking the service down.
    /// </summary>
    public string Provider { get; set; } = DefaultProvider;

    /// <summary>Settings for the morph-idm provider; ignored unless it is selected.</summary>
    public MorphIdmOptions MorphIdm { get; set; } = new();
}

/// <summary>
/// Connection settings for the morph-idm <c>get-roles</c> endpoint.
/// </summary>
public sealed class MorphIdmOptions
{
    /// <summary>Base URL of the morph-idm service, e.g. <c>https://idm.internal</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Path of the operation-set endpoint, appended to <see cref="BaseUrl"/>.</summary>
    public string GetRolesPath { get; set; } = "/api/1/morph-idm/functions/get-roles";

    /// <summary>
    /// Request timeout. Kept short by default: this call sits on the critical path of every authorized
    /// read, and a slow provider is indistinguishable from a denial to the caller waiting on it.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Retry attempts on transient failure. Low by default, for the same reason as the timeout.</summary>
    public int MaxRetryAttempts { get; set; } = 1;

    /// <summary>Base delay between retries; grows exponentially per attempt.</summary>
    public int RetryDelayMilliseconds { get; set; } = 200;

    /// <summary>Consecutive transient failures before the circuit opens.</summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 20;

    /// <summary>How long the circuit stays open.</summary>
    public int CircuitBreakerTimeoutSeconds { get; set; } = 30;

    /// <summary>Whether to validate the server's TLS certificate. Disable only in development.</summary>
    public bool ValidateSsl { get; set; } = true;
}
