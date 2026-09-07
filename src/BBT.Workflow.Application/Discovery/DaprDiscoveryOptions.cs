namespace BBT.Workflow.Discovery;

/// <summary>
/// Settings for the Dapr discovery provider (<c>ServiceDiscovery:Dapr</c>). Only consulted when
/// <see cref="ServiceDiscoveryOptions.Provider"/> is <c>"dapr"</c>.
/// </summary>
public sealed class DaprDiscoveryOptions
{
    /// <summary>Configuration section name, relative to <c>ServiceDiscovery</c>.</summary>
    public const string SectionName = "Dapr";

    /// <summary>
    /// Template for the target's Kubernetes namespace, used to build the cross-namespace app-id
    /// <c>{appId}.{namespace}</c>. The only token is <c>{domain}</c>; everything else is literal.
    /// Example: <c>"preprod-vnext-{domain}"</c> → domain <c>credit</c> resolves to
    /// <c>vnext-credit-app.preprod-vnext-credit</c>.
    /// <para>
    /// The environment prefix is deliberately NOT a separate setting: the Helm chart renders the
    /// whole template from <c>.Release.Namespace</c> (the caller's own namespace minus its
    /// <c>-vnext-{appDomain}</c> suffix), so the code never has to know how environments are
    /// named. <c>ASPNETCORE_ENVIRONMENT</c> is not usable for this — it is a runtime mode
    /// (<c>Development</c>/<c>Staging</c>/<c>Production</c>), not the namespace naming scheme
    /// (<c>test</c>/<c>stage</c>/<c>preprod</c>/…), and the two must be free to differ.
    /// </para>
    /// <para>
    /// Empty (the default) means single-namespace: the bare app-id is used and Dapr resolves it
    /// in the caller's own namespace. That is the correct behaviour for local docker-compose.
    /// </para>
    /// <para>
    /// This is NOT redundant with the resolver template's <c>{{.Namespace}}</c>. That variable
    /// does not discover the target's namespace — it echoes back whatever Dapr was told.
    /// <c>requestAppIDAndNamespace</c> defaults the namespace to the CALLER's own when the
    /// app-id carries no dot, so a cross-namespace call must pass
    /// <c>vnext-{domain}-app.{prefix}-vnext-{domain}</c> explicitly. This template is how that
    /// suffix gets built.
    /// </para>
    /// </summary>
    public string NamespaceTemplate { get; set; } = string.Empty;

    /// <summary>
    /// When true (default) a non-empty <c>appId</c> in the registry entry overrides the
    /// convention — the escape hatch for a domain whose app-id does not follow
    /// <c>vnext-{domain}-app</c>.
    /// <para>
    /// Only consulted when <see cref="RequireRegistryEntry"/> is true, which is <b>not</b> the
    /// default: with the registry not being read at all there is nothing to prefer, and a domain
    /// that needs a non-conventional app-id in that mode is served by <see cref="DomainOverrides"/>
    /// instead. Turning this on without also turning on <see cref="RequireRegistryEntry"/> has no
    /// effect.
    /// </para>
    /// </summary>
    public bool PreferRegistryAppId { get; set; } = true;

    /// <summary>
    /// Whether the registry is consulted at all.
    /// <para>
    /// False (default): the registry is <b>never</b> read and resolution is pure convention (plus
    /// any <see cref="DomainOverrides"/>). No network call — the point of moving resolution to
    /// Dapr was to stop asking the discovery domain on every cross-domain call — and immune to a
    /// registry outage. The trade is that a registry-supplied app-id override cannot be seen
    /// (deliberately: "skip the registry" and "read the registry for the override" cannot both be
    /// true), and an unregistered domain is only detected when the sidecar cannot reach it
    /// (<c>ERR_DIRECT_INVOKE</c>, surfaced as a transient remote error).
    /// </para>
    /// <para>
    /// True: the registry is read on resolution (cached for <see cref="CacheSeconds"/>), an
    /// unregistered domain fails at resolution with <c>DomainEndpointNotFound</c> — keeping the
    /// registry authoritative about which domains exist — and <see cref="PreferRegistryAppId"/>
    /// can apply its override. Environments whose app-ids do not follow the convention (the local
    /// vnext-runtime template names them <c>vnext-app-{domain}</c>) need this, or a
    /// <see cref="DomainOverrides"/> entry per domain.
    /// </para>
    /// </summary>
    public bool RequireRegistryEntry { get; set; } = false;

    /// <summary>
    /// Positive-result cache lifetime in seconds (default 60; 0 disables).
    /// <para>
    /// Safe here in a way it was not before. The no-cache decision existed so a MOVED address
    /// could never be masked by a stale entry — but under this provider the registry no longer
    /// supplies the address at all (Dapr Name Resolution does, per call). A stale entry can now
    /// only stale the optional <see cref="PreferRegistryAppId"/> override. Failures are never
    /// cached, so a domain that registers later is picked up immediately.
    /// </para>
    /// </summary>
    public int CacheSeconds { get; set; } = 60;

    /// <summary>
    /// Per-domain overrides, keyed by domain name. A value of <c>"url"</c> forces that domain
    /// back onto the HTTP provider; any other value is used verbatim as the target app-id.
    /// <para>
    /// This is the rollout and rollback dial: one domain can be moved onto Dapr, or pulled back
    /// off it, without changing the global <see cref="ServiceDiscoveryOptions.Provider"/>.
    /// </para>
    /// </summary>
    public Dictionary<string, string> DomainOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sentinel <see cref="DomainOverrides"/> value that forces the HTTP provider.</summary>
    public const string UrlOverride = "url";

    /// <summary>
    /// Renders <see cref="NamespaceTemplate"/> for <paramref name="domain"/>, or null when no
    /// template is configured.
    /// </summary>
    public string? ResolveNamespace(string domain)
    {
        if (string.IsNullOrWhiteSpace(NamespaceTemplate))
            return null;

        return NamespaceTemplate
            .Replace("{domain}", domain.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
            .Trim()
            .Trim('-');
    }
}
