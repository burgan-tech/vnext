using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Uow;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances.Related;

public class RelatedInstanceQueryAppServiceTests : IDisposable
{
    private static readonly Guid TargetId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public RelatedInstanceQueryAppServiceTests()
    {
        // Required for PostSharp SchemaValidation aspect used by Instance.AddData (via TargetInstance()).
        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockUoW));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mockUoWManager);
        services.AddSingleton(Substitute.For<BBT.Workflow.Caching.IComponentCacheStore>());
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
    }

    private static RelatedInstanceRef Reference() =>
        new(TargetId, "lending", "loan-application", "2.1.0");

    private static Instance TargetInstance()
    {
        var instance = Instance.Create(TargetId, "loan-application", "2.1.0", "customer-42");
        instance.SeedData(
            Guid.NewGuid(),
            new JsonData(JsonSerializer.SerializeToElement(new
            {
                creditLimit = 50000,
                restrictedField = "only-for-officers"
            })));
        return instance;
    }

    private static RelatedInstanceQueryAppService CreateService(Instance? instance)
    {
        var repository = Substitute.For<IInstanceRepository>();
        repository.FindByIdentifierAsReadOnlyAsync(TargetId.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);
        return new RelatedInstanceQueryAppService(repository, NullLogger<RelatedInstanceQueryAppService>.Instance);
    }

    [Fact]
    public async Task ReadAsync_ShouldReturnSuccessWithNull_WhenInstanceDoesNotExist()
    {
        var service = CreateService(null);

        var result = await service.ReadAsync(Reference(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task ReadAsync_ShouldProjectIdentityAndStatus()
    {
        var service = CreateService(TargetInstance());

        var result = await service.ReadAsync(Reference(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var snapshot = result.Value.ShouldNotBeNull();
        snapshot.InstanceId.ShouldBe(TargetId);
        snapshot.Key.ShouldBe("customer-42");
        snapshot.Domain.ShouldBe("lending");
        snapshot.Flow.ShouldBe("loan-application");
        snapshot.FlowVersion.ShouldBe("2.1.0");
        snapshot.Status.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ReadAsync_ShouldReturnDataUnfiltered()
    {
        var service = CreateService(TargetInstance());

        var result = await service.ReadAsync(Reference(), CancellationToken.None);

        // No x-roles filtering: every field of the stored payload survives, including one a caller
        // without the right role could not see through the Data function.
        var data = (IDictionary<string, object?>)result.Value!.Data!;
        data.ShouldContainKey("creditLimit");
        data.ShouldContainKey("restrictedField");
    }

    [Fact]
    public async Task ReadAsync_ShouldNotRequireRolesOrHeaders()
    {
        // Regression guard: the internal read path must work with no caller identity at all,
        // which is the situation in scheduled, automatic, event and background-job contexts.
        var service = CreateService(TargetInstance());

        var result = await service.ReadAsync(Reference(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReadManyAsync_ShouldOmitMissingInstances()
    {
        var missingId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var targetInstance = TargetInstance();
        var repository = Substitute.For<IInstanceRepository>();
        repository.FindByIdsAsReadOnlyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([targetInstance]);
        var service = new RelatedInstanceQueryAppService(
            repository, NullLogger<RelatedInstanceQueryAppService>.Instance);

        var result = await service.ReadManyAsync(
            [Reference(), new RelatedInstanceRef(missingId, "lending", "loan-application", "2.1.0")],
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Count.ShouldBe(1);
        result.Value[0].InstanceId.ShouldBe(TargetId);
    }

    [Fact]
    public async Task ReadManyAsync_ShouldIssueOneQuery_NotOnePerReference()
    {
        // The batch API exists to avoid N+1; pin it.
        var secondId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
        var repository = Substitute.For<IInstanceRepository>();
        var target = TargetInstance();
        repository.FindByIdsAsReadOnlyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([target]);
        var service = new RelatedInstanceQueryAppService(
            repository, NullLogger<RelatedInstanceQueryAppService>.Instance);

        await service.ReadManyAsync(
            [Reference(), new RelatedInstanceRef(secondId, "lending", "loan-application", "2.1.0")],
            CancellationToken.None);

        await repository.Received(1).FindByIdsAsReadOnlyAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
        await repository.DidNotReceiveWithAnyArgs().FindByIdentifierAsReadOnlyAsync(default!, default);
    }

    [Fact]
    public async Task ReadManyAsync_ShouldFailTheWholeBatch_WhenTheQueryThrows()
    {
        // Atomic failure: a partial set would let a script see four children when there are five,
        // with no way to tell — treating a fault as absence.
        var repository = Substitute.For<IInstanceRepository>();
        repository.FindByIdsAsReadOnlyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<List<Instance>>(_ => throw new InvalidOperationException("connection reset"));
        var service = new RelatedInstanceQueryAppService(
            repository, NullLogger<RelatedInstanceQueryAppService>.Instance);

        var result = await service.ReadManyAsync([Reference()], CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain("connection reset");
    }
}
