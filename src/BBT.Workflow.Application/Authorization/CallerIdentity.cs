namespace BBT.Workflow.Authorization;

/// <summary>
/// The public caller's identity as carried across a hop that has no ambient caller of its own (the
/// human-task leaf hop, where the leaf may be resolved on another host). <see cref="ActorUserName"/>
/// is matched by <c>$InstanceStarter</c> / <c>$PreviousUser</c>; <see cref="SubjectUserName"/> by
/// <c>$InstanceBehalfOfStarter</c> / <c>$PreviousBehalfOfUser</c>. Self-asserted, like the roles that
/// travel in the same request body.
/// </summary>
public sealed record CallerIdentity(string? ActorUserName, string? SubjectUserName);
