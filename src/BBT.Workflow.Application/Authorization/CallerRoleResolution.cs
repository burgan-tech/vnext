namespace BBT.Workflow.Authorization;

/// <summary>How a caller's role set came about; row security tells "failed" from "no roles" with it.</summary>
public enum CallerRoleOutcome
{
    /// <summary>Roles were resolved by the provider (non-empty).</summary>
    Resolved,

    /// <summary>Roles came from the request's <c>role</c> header.</summary>
    FromHeader,

    /// <summary>Resolution succeeded and the caller holds no roles.</summary>
    Empty,

    /// <summary>The provider chose not to resolve (nothing to look up).</summary>
    Skipped,

    /// <summary>Resolution failed or cannot be verified; the role set must not be trusted.</summary>
    Failed
}

/// <summary>A caller's role set together with how it was obtained.</summary>
/// <param name="Roles">The roles; empty when none or when <paramref name="Outcome"/> is <see cref="CallerRoleOutcome.Failed"/>.</param>
/// <param name="Outcome">How the set came about.</param>
/// <param name="FailureKind">Coarse failure category (never a value or exception text); null unless failed.</param>
public sealed record CallerRoleResolution(
    string[] Roles,
    CallerRoleOutcome Outcome,
    string? FailureKind = null);
