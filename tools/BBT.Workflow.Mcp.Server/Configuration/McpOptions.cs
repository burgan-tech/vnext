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
    /// Per-domain fixed API keys used to authorize inbound MCP clients (domain → key). A client may
    /// only use tools for a domain when it presents that domain's key. <b>Empty ⇒ authorization is
    /// disabled</b> (open) — convenient for local development.
    /// </summary>
    public Dictionary<string, string> DomainApiKeys { get; set; } = new();

    /// <summary>
    /// The API key presented by the client under the <b>stdio</b> transport, where there is no inbound
    /// HTTP request to carry an <c>Authorization</c> header. Ignored on the HTTP transport (the
    /// <c>Authorization: Bearer</c> header is used there instead).
    /// </summary>
    public string? ClientApiKey { get; set; }

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
