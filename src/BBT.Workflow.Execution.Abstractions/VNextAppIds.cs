namespace BBT.Workflow.Execution;

/// <summary>
/// Single source of truth for vNext Dapr application IDs.
/// </summary>
/// <remarks>
/// <para>
/// Every vNext component's Dapr app-id is derived from the domain by a fixed convention,
/// mirroring <c>vnext.daprAnnotations</c> in the Helm chart (<c>charts/vnext/templates/_helpers.tpl</c>).
/// The chart is the deployment-time authority; these templates must stay byte-identical to it.
/// </para>
/// <para>
/// Why this type exists: the same defaults used to be hardcoded at nine call sites and had
/// drifted. <c>"vnext-app"</c> appeared in six invokers plus the inbox forwarder — correct only
/// for <c>appDomain: core</c> and silently wrong for every other domain. Worse,
/// <c>ExecutionApi:AppId</c> defaulted to <c>"vnext-execution"</c> in code while every
/// appsettings.json and compose file said <c>vnext-execution-app</c>; the mismatch was masked
/// purely by configuration always being present, so it would surface only where config was
/// missing — the one case a default exists for.
/// </para>
/// <para>
/// Deliberately free of <c>IConfiguration</c>: the Domain layer does not reference
/// Microsoft.Extensions.Configuration. Callers read their own override and pass it to the
/// <c>*OrDefault</c> overloads, which keeps the precedence rule (explicit config wins over
/// convention) in one place without pulling configuration into Domain.
/// </para>
/// <para>
/// <c>DAPR_APP_ID</c> is intentionally NOT a fallback here. That variable is the process's OWN
/// identity — used for self-registration (<c>DomainRegistrationService</c>) and for the subflow
/// callback app-id (<c>SubflowStarter</c>, <c>SubProcessTaskExecutor</c>) — so using it to
/// resolve a TARGET app-id would make a component address itself.
/// </para>
/// </remarks>
public static class VNextAppIds
{
    /// <summary>Orchestration host app-id template. Chart: <c>printf "vnext-%s-app"</c>.</summary>
    public const string OrchestratorTemplate = "vnext-{0}-app";

    /// <summary>Execution host app-id template. Chart: <c>printf "vnext-%s-execution-app"</c>.</summary>
    public const string ExecutionTemplate = "vnext-{0}-execution-app";

    /// <summary>Inbox worker app-id template. Chart: <c>printf "vnext-%s-worker-inbox-app"</c>.</summary>
    public const string WorkerInboxTemplate = "vnext-{0}-worker-inbox-app";

    /// <summary>Outbox worker app-id template. Chart: <c>printf "vnext-%s-worker-outbox-app"</c>.</summary>
    public const string WorkerOutboxTemplate = "vnext-{0}-worker-outbox-app";

    /// <summary>DbMigrator app-id template. Chart: <c>printf "vnext-%s-db-migrator-app"</c>.</summary>
    public const string DbMigratorTemplate = "vnext-{0}-db-migrator-app";

    /// <summary>Configuration keys that override the conventional app-ids.</summary>
    public static class ConfigKeys
    {
        /// <summary>Overrides the orchestration host app-id.</summary>
        public const string Orchestrator = "OrchestrationApi:AppId";

        /// <summary>Overrides the execution host app-id.</summary>
        public const string Execution = "ExecutionApi:AppId";

        /// <summary>
        /// The domain this process serves. Same source <c>RuntimeInfoProvider</c> reads,
        /// so the two can never disagree; guaranteed present because
        /// <c>RuntimeInfoProvider</c> throws at startup when it is missing.
        /// </summary>
        public const string AppDomain = "APP_DOMAIN";

        /// <summary>
        /// This process's OWN Dapr app-id. Not a fallback for resolving a target — see the
        /// remarks on <see cref="VNextAppIds"/>.
        /// </summary>
        public const string SelfAppId = "DAPR_APP_ID";
    }

    /// <summary>Builds the orchestration host app-id for <paramref name="domain"/>.</summary>
    public static string Orchestrator(string domain) => Build(OrchestratorTemplate, domain);

    /// <summary>Builds the execution host app-id for <paramref name="domain"/>.</summary>
    public static string Execution(string domain) => Build(ExecutionTemplate, domain);

    /// <summary>Builds the inbox worker app-id for <paramref name="domain"/>.</summary>
    public static string WorkerInbox(string domain) => Build(WorkerInboxTemplate, domain);

    /// <summary>Builds the outbox worker app-id for <paramref name="domain"/>.</summary>
    public static string WorkerOutbox(string domain) => Build(WorkerOutboxTemplate, domain);

    /// <summary>Builds the DbMigrator app-id for <paramref name="domain"/>.</summary>
    public static string DbMigrator(string domain) => Build(DbMigratorTemplate, domain);

    /// <summary>
    /// Returns <paramref name="configured"/> when it carries a value, otherwise the
    /// conventional orchestration app-id for <paramref name="domain"/>.
    /// </summary>
    public static string OrchestratorOrDefault(string? configured, string? domain) =>
        Coalesce(configured, OrchestratorTemplate, domain);

    /// <summary>
    /// Returns <paramref name="configured"/> when it carries a value, otherwise the
    /// conventional execution app-id for <paramref name="domain"/>.
    /// </summary>
    public static string ExecutionOrDefault(string? configured, string? domain) =>
        Coalesce(configured, ExecutionTemplate, domain);

    private static string Coalesce(string? configured, string template, string? domain) =>
        string.IsNullOrWhiteSpace(configured) ? Build(template, domain) : configured.Trim();

    /// <summary>
    /// Applies a template to a domain. App-ids are lower-cased because Dapr resolves them
    /// into DNS names, and <see cref="Uri"/> lower-cases hosts on the invocation path — so an
    /// upper-cased domain would produce an app-id that no longer matches the SPIFFE identity
    /// Dapr expects (<c>spiffe://.../ns/{namespace}/{app-id}</c>) and mTLS would reject it.
    /// </summary>
    private static string Build(string template, string? domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return string.Format(template, domain.Trim().ToLowerInvariant());
    }
}
