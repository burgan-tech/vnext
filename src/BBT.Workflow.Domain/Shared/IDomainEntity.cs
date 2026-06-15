namespace BBT.Workflow;

public interface IDomainEntity : IHasKey, IHasVersion, IHasDomain
{
    /// <summary>
    /// Logical component-type discriminator (e.g. "sys-flows", "sys-tasks").
    /// Mirrors <see cref="ComponentTypeKey"/> on the instance side - exposed as a
    /// static abstract so type-only consumers (like generic caches/indexers) can
    /// resolve the key without constructing an instance.
    /// </summary>
    static abstract string ComponentTypeKey { get; }

    /// <summary>
    /// Instance-side accessor for the component-type discriminator. Implementations
    /// typically forward to <see cref="ComponentTypeKey"/>.
    /// </summary>
    string ComponentKey { get; }
}