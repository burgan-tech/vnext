using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Authorization.SubEndpoints;

/// <summary>
/// Decides whether a call to a runtime-internal endpoint may proceed (vnext#1265).
/// </summary>
public interface ISubEndpointGate
{
    /// <summary>
    /// Admits a call with a valid runtime-caller token, otherwise decides by the configured grants.
    /// In <see cref="SubEndpointGateMode.Observe"/> a refusal is logged and admitted. A failure is
    /// 403 <c>Authorization:110001</c>, or 503 <c>Authorization:110006</c> when the caller's roles
    /// could not be resolved.
    /// </summary>
    /// <param name="target">The operation and route values of the call.</param>
    /// <param name="token">The runtime-caller token the request arrived with, if any.</param>
    /// <param name="headers">The request headers (the token already removed), for role resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result> AuthorizeAsync(
        RuntimeCallTarget target,
        string? token,
        IReadOnlyDictionary<string, string?> headers,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SubEndpointGate : ISubEndpointGate
{
    private readonly SubEndpointGateOptions _options;
    private readonly IReadOnlyList<RoleGrant> _grants;
    private readonly bool _needsCallerRoles;
    private readonly IRuntimeCallerVerifier? _verifier;
    private readonly ICallerRoleResolver _roleResolver;
    private readonly ILogger<SubEndpointGate> _logger;

    /// <summary>Creates the gate over the validated options.</summary>
    public SubEndpointGate(
        IOptions<SubEndpointGateOptions> options,
        ICallerRoleResolver roleResolver,
        ILogger<SubEndpointGate> logger,
        IRuntimeCallerVerifier? verifier = null)
    {
        _options = options.Value;
        _grants = SubEndpointGrants.Build(_options.Grants, []);
        _needsCallerRoles = SubEndpointGrants.NeedsCallerRoles(_grants);
        _verifier = verifier;
        _roleResolver = roleResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> AuthorizeAsync(
        RuntimeCallTarget target,
        string? token,
        IReadOnlyDictionary<string, string?> headers,
        CancellationToken cancellationToken = default)
    {
        var mode = _options.ModeFor(target.Operation);
        if (mode == SubEndpointGateMode.Off)
            return Result.Ok();

        var activity = Activity.Current;
        var verdict = _verifier?.Verify(token, target)
                      ?? new RuntimeCallerTokenVerdict(string.IsNullOrWhiteSpace(token)
                          ? RuntimeCallerTokenStatus.Missing
                          : RuntimeCallerTokenStatus.UnknownKey);
        TagVerdict(activity, verdict);

        if (verdict.IsValid)
        {
            Tag(activity, "admitted_runtime");
            return Result.Ok();
        }

        var grantDecision = await DecideByGrantsAsync(headers, cancellationToken);
        if (grantDecision == GrantDecision.Admit)
        {
            Tag(activity, "admitted_grant");
            return Result.Ok();
        }

        var reason = grantDecision == GrantDecision.Unavailable ? "roles_unavailable" : "grant_denied";
        var callerClass = verdict.CallerClass ?? "-";
        var callerDomain = verdict.CallerDomain ?? "-";
        // Route values are decoded URL text a client chose: keep them to one printable line in the log.
        var (domain, workflow, instance) = (LogText(target.Domain), LogText(target.Workflow), LogText(target.Instance));
        if (mode == SubEndpointGateMode.Observe)
        {
            Tag(activity, "would_refuse", reason);
            _logger.SubEndpointGateWouldRefuse(target.Operation, domain, workflow, instance, reason, callerClass,
                callerDomain, verdict.Reason);
            return Result.Ok();
        }

        Tag(activity, grantDecision == GrantDecision.Unavailable ? "unavailable" : "refused", reason);
        _logger.SubEndpointGateRefused(target.Operation, domain, workflow, instance, reason, callerClass,
            callerDomain, verdict.Reason);
        return grantDecision == GrantDecision.Unavailable
            ? Result.Fail(WorkflowErrors.SubEndpointCallerRolesUnavailable())
            : Result.Fail(WorkflowErrors.SubEndpointAccessDenied(target.Operation));
    }

    private async Task<GrantDecision> DecideByGrantsAsync(
        IReadOnlyDictionary<string, string?> headers,
        CancellationToken cancellationToken)
    {
        string[] roles = [];
        if (_needsCallerRoles)
        {
            var resolution = await _roleResolver.ResolveRoleSetAsync(headers, cancellationToken);
            if (resolution.Outcome == CallerRoleOutcome.Failed)
                return GrantDecision.Unavailable;
            roles = resolution.Roles;
        }

        return TransitionAuthorizationManager.EvaluateRolesStatic(roles, _grants)
            ? GrantDecision.Admit
            : GrantDecision.Deny;
    }

    private static string LogText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "-";

        var text = value.Length > 128 ? value[..128] : value;
        return text.Any(char.IsControl)
            ? new string(text.Select(c => char.IsControl(c) ? '?' : c).ToArray())
            : text;
    }

    private static void TagVerdict(Activity? activity, RuntimeCallerTokenVerdict verdict)
    {
        if (activity is null)
            return;

        activity.SetTag(TelemetryConstants.TagNames.RuntimeCallerVerdict, verdict.Reason);
        if (verdict.CallerClass is not null)
            activity.SetTag(TelemetryConstants.TagNames.RuntimeCallerClass, verdict.CallerClass);
        if (verdict.CallerDomain is not null)
            activity.SetTag(TelemetryConstants.TagNames.RuntimeCallerDomain, verdict.CallerDomain);
        if (verdict.KeyId is not null)
            activity.SetTag(TelemetryConstants.TagNames.RuntimeCallerKeyId, verdict.KeyId);
        if (verdict.SkewSeconds is { } skew)
            activity.SetTag(TelemetryConstants.TagNames.RuntimeCallerSkewSeconds, skew);
    }

    private static void Tag(Activity? activity, string decision, string? reason = null)
    {
        activity?.SetTag(TelemetryConstants.TagNames.GateDecision, decision);
        if (reason is not null)
            activity?.SetTag(TelemetryConstants.TagNames.GateReason, reason);
    }

    private enum GrantDecision
    {
        Admit,
        Deny,
        Unavailable
    }
}
