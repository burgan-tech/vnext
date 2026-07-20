namespace BBT.Workflow.Instances.Events;

public sealed record TerminationContext(
    TerminationOrigin Origin,
    Guid InitiatorInstanceId,
    Guid CascadeId)
{
    public static TerminationContext Direct(Guid instanceId) =>
        new(TerminationOrigin.Direct, instanceId, Guid.NewGuid());

    public TerminationContext AsParentCascade() => this with
    {
        Origin = TerminationOrigin.ParentCascade
    };
}
