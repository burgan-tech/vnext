namespace BBT.Workflow.Execution.RuntimeCaller;

/// <summary>
/// The runtime-internal operations a runtime-caller token can be minted for. One per gated endpoint.
/// </summary>
public static class RuntimeCallOperations
{
    /// <summary><c>POST {domain}/workflows/{workflow}/sub/instances/start</c>.</summary>
    public const string SubStart = "sub-start";

    /// <summary><c>POST {domain}/workflows/{workflow}/instances/{instance}/complete</c>.</summary>
    public const string Complete = "complete";
}

/// <summary>
/// Which runtime path minted a token. Attribution only — it is signed, so it cannot be altered,
/// but every key holder can claim any class; it never grants anything.
/// </summary>
public static class RuntimeCallerClasses
{
    /// <summary>Cross-domain SubFlow start (<c>SubflowStarter</c> → remote command service).</summary>
    public const string SubFlow = "subflow";

    /// <summary>Cross-domain SubProcess start (<c>SubProcessRemoteInvoker</c>).</summary>
    public const string SubProcess = "subprocess";

    /// <summary>Cross-domain completion relay (post-commit, remote command service).</summary>
    public const string Relay = "relay";

    /// <summary>Same-domain completion backup (Inbox worker forwarder).</summary>
    public const string Inbox = "inbox";
}

/// <summary>
/// What a runtime-caller token is bound to: the operation and the target route values. The sender
/// knows them before it builds a URL and the receiver reads them from routing, so the binding does
/// not depend on how the URL was spelled (<c>api/v1</c> vs <c>api/v1.0</c>, a BaseUrl prefix, a
/// proxy rewrite, Dapr service invocation vs plain HTTP).
/// </summary>
/// <param name="Operation">One of <see cref="RuntimeCallOperations"/>.</param>
/// <param name="Domain">The target domain route value.</param>
/// <param name="Workflow">The target workflow route value.</param>
/// <param name="Instance">The target instance route value; null for <see cref="RuntimeCallOperations.SubStart"/>.</param>
public readonly record struct RuntimeCallTarget(string Operation, string Domain, string Workflow, string? Instance)
{
    /// <summary>The target of a sub-start call.</summary>
    public static RuntimeCallTarget SubStart(string domain, string workflow)
        => new(RuntimeCallOperations.SubStart, domain, workflow, null);

    /// <summary>The target of a completion call for the parent instance <paramref name="instanceId"/>.</summary>
    public static RuntimeCallTarget Complete(string domain, string workflow, Guid instanceId)
        => new(RuntimeCallOperations.Complete, domain, workflow, instanceId.ToString("D"));

    /// <summary>The target of a completion call for the parent instance route value <paramref name="instance"/>.</summary>
    public static RuntimeCallTarget Complete(string domain, string workflow, string instance)
        => new(RuntimeCallOperations.Complete, domain, workflow, instance);
}
