namespace BBT.Workflow.Authorization;

/// <summary>
/// The three-valued (Kleene) answer to "does this role grant, or this leaf of it, match the caller?".
/// <para>
/// <see cref="Unknown"/> exists for one case: a role-bound leaf (a static role or a <c>$role.</c>
/// reference) evaluated for a caller that carries no roles at all. "Nothing matched" is not evidence
/// there, so the leaf is neither a match nor a non-match. Identity leaves (<c>$InstanceStarter</c>,
/// <c>$PreviousUser</c>, <c>$user.</c>, …) compare the caller's identity and are always
/// <see cref="Yes"/> or <see cref="No"/>.
/// </para>
/// </summary>
internal enum GrantMatch : byte
{
    No = 0,
    Yes = 1,
    Unknown = 2
}
