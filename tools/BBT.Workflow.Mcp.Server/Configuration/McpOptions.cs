namespace BBT.Workflow.Mcp.Configuration;

/// <summary>
/// Configuration for the <c>vnext-runtime</c> MCP server. Bound from the <c>Mcp</c>
/// configuration section (appsettings / environment variables / command line).
/// </summary>
public sealed class McpOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Mcp";

    /// <summary>
    /// Base URL of the Orchestration HTTP API the server proxies to.
    /// </summary>
    public string OrchestrationBaseUrl { get; set; } = "http://localhost:4201";

    /// <summary>
    /// The single vNext domain this MCP instance serves. Each instance is single-domain (like
    /// <see cref="OrchestrationBaseUrl"/>), so the domain is configured here rather than passed on every
    /// tool call. Required for component/runtime tools; when unset they return a clear configuration error.
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// When <c>true</c>, mutating tools (start_instance, run_transition, …) are registered.
    /// Default <c>false</c> to prevent accidental writes from CI/agents.
    /// </summary>
    public bool AllowMutations { get; set; }

    /// <summary>
    /// When <c>true</c>, the <c>get_mapping_code</c> tool (which returns executable
    /// <c>.csx</c> source) is registered. Default <c>true</c>.
    /// </summary>
    public bool AllowCodeRead { get; set; } = true;

    /// <summary>
    /// The fixed API key that inbound MCP clients must present (as <c>Authorization: Bearer &lt;key&gt;</c>)
    /// on the <b>HTTP</b> transport. Each MCP instance is single-domain, so a single key suffices.
    /// <b>Empty/null ⇒ authorization is disabled</b> (open) — convenient for local development. The
    /// stdio transport is not gated (the process is launched by the trusted local user).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The npm package holding the offline <c>vnext-meta</c> JSON files. Loaded once at startup
    /// from the public registry — no filesystem dependency.
    /// </summary>
    public string MetaPackageName { get; set; } = "@burgan-tech/vnext-meta";

    /// <summary>
    /// The <b>pinned</b> version of <see cref="MetaPackageName"/> to load (e.g. <c>0.0.61</c>).
    /// Never use <c>latest</c> — a moving version causes non-reproducible behavior in CI.
    /// </summary>
    public string MetaPackageVersion { get; set; } = "0.0.61";
}
