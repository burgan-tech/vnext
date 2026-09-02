using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Testing;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

public abstract class InstanceRepositoryTests<TEntry> : DomainTestBase<TEntry>
    where TEntry : ModuleEntryPointBase, new()
{
    protected IInstanceRepository Repository => GetRequiredService<IInstanceRepository>();

    [Fact]
    public async Task FindLeanByIdAsync_ShouldFindByPrimaryKey()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0", "lean-id-key");
        await Repository.InsertAsync(instance, true);

        var found = await Repository.FindLeanByIdAsync(instance.Id, CancellationToken.None);

        found.ShouldNotBeNull();
        found.Id.ShouldBe(instance.Id);
        found.Key.ShouldBe("lean-id-key");
    }

    [Fact]
    public async Task FindLeanByIdAsync_ShouldReturnNullForUnknownId()
    {
        var found = await Repository.FindLeanByIdAsync(Guid.NewGuid(), CancellationToken.None);

        found.ShouldBeNull();
    }

    /// <summary>
    /// The reason the method exists: unlike the identifier resolvers, a PK miss must NOT fall back
    /// to comparing the guid string against <see cref="Instance.Key"/>. An instance whose Key
    /// happens to be the probed guid's string is reachable through
    /// <see cref="IInstanceRepository.FindByIdentifierAsync"/> but must stay invisible here.
    /// </summary>
    [Fact]
    public async Task FindLeanByIdAsync_ShouldNotFallBackToKey()
    {
        var probedId = Guid.NewGuid();
        var keyImpostor = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0", probedId.ToString());
        await Repository.InsertAsync(keyImpostor, true);

        var byId = await Repository.FindLeanByIdAsync(probedId, CancellationToken.None);
        var byIdentifier = await Repository.FindByIdentifierAsync(probedId.ToString(), CancellationToken.None);

        byId.ShouldBeNull();
        // Contrast pin: the generic resolver's key fallback DOES find the impostor.
        byIdentifier.ShouldNotBeNull();
        byIdentifier.Id.ShouldBe(keyImpostor.Id);
    }
}
