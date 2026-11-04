using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.HumanTask;
using BBT.Workflow.Instances.Related;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using Definitions = BBT.Workflow.Definitions;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Pins that every same-domain read a task can reach runs in its OWN unit of work, not only its own DI
/// scope. A DbContext is bound to the ambient (AsyncLocal) unit of work and keyed by schema, so same-order
/// tasks — parallel branches of one pipeline — reading one flow through a scope-only gateway shared a
/// context and failed with "A second operation was started on this context instance" (vnext#1161).
/// </summary>
public sealed class LocalReadGatewayUnitOfWorkIsolationTests
{
    private readonly IUnitOfWorkManager _uowManager = Substitute.For<IUnitOfWorkManager>();
    private readonly List<UnitOfWorkOptions> _begun = [];
    private readonly List<IUnitOfWork> _units = [];
    private int _open;

    /// <summary>Set by a substituted read service: was an isolated unit of work open when it ran?</summary>
    private bool _readRanInsideIsolatedUnitOfWork;

    private readonly IInstanceQueryAppService _queryService = Substitute.For<IInstanceQueryAppService>();
    private readonly IInstanceRepository _repository = Substitute.For<IInstanceRepository>();
    private readonly IRelatedInstanceQueryAppService _relatedService = Substitute.For<IRelatedInstanceQueryAppService>();
    private readonly IHumanTaskLeafResolver _leafResolver = Substitute.For<IHumanTaskLeafResolver>();

    private readonly IServiceScopeFactory _scopeFactory;

    public LocalReadGatewayUnitOfWorkIsolationTests()
    {
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions?>()).Returns(call =>
        {
            var options = call.Arg<UnitOfWorkOptions?>()!;
            var unit = Substitute.For<IUnitOfWork>();
            unit.DisposeAsync().Returns(_ =>
            {
                Interlocked.Decrement(ref _open);
                return ValueTask.CompletedTask;
            });
            lock (_begun)
            {
                _begun.Add(options);
                _units.Add(unit);
            }
            Interlocked.Increment(ref _open);
            return unit;
        });

        var schema = Substitute.For<ICurrentSchema>();
        schema.Change(Arg.Any<string>()).Returns(Substitute.For<IDisposable>());

        var cache = Substitute.For<IComponentCacheStore>();
        cache.GetFlowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(Definitions.Workflow.Create()));

        var services = new ServiceCollection();
        services.AddSingleton(_uowManager);
        services.AddSingleton(schema);
        services.AddSingleton(cache);
        services.AddSingleton(_queryService);
        services.AddSingleton(_repository);
        services.AddSingleton(_relatedService);
        services.AddSingleton(_leafResolver);
        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private void RecordIsolation() =>
        _readRanInsideIsolatedUnitOfWork = Volatile.Read(ref _open) > 0
            && _begun.Count > 0
            && _begun.All(o => o.Scope == UnitOfWorkScopeOption.RequiresNew && !o.IsTransactional);

    [Fact]
    public async Task GetInstanceList_RunsInsideItsOwnNonTransactionalUnitOfWork_AndCommitsIt()
    {
        _queryService.GetInstanceListAsync(Arg.Any<GetInstanceListInput>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                RecordIsolation();
                return Result<InstanceListWithGroupsResponse<GetInstanceOutput>>.Ok(new());
            });

        var result = await new LocalInstanceQueryGateway(_scopeFactory).GetInstanceListAsync(
            new GetInstanceListInput { Domain = "core", Workflow = "target" });

        result.IsSuccess.ShouldBeTrue();
        _readRanInsideIsolatedUnitOfWork.ShouldBeTrue();
        await _units.Single().Received(1).CommitAsync(Arg.Any<CancellationToken>());
        Volatile.Read(ref _open).ShouldBe(0);
    }

    [Fact]
    public async Task GetInstance_RunsInsideItsOwnUnitOfWork()
    {
        _queryService.GetInstanceAsync(Arg.Any<GetInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                RecordIsolation();
                return ConditionalResult<GetInstanceOutput>.Fail(Error.NotFound("x", "x"));
            });

        await new LocalInstanceQueryGateway(_scopeFactory).GetInstanceAsync(
            new GetInstanceInput { Domain = "core", Workflow = "target", Instance = "key-1" });

        _readRanInsideIsolatedUnitOfWork.ShouldBeTrue();
    }

    [Fact]
    public async Task GetInstanceData_RunsInsideItsOwnUnitOfWork()
    {
        _queryService.GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                RecordIsolation();
                return ConditionalResult<GetInstanceDataOutput>.Fail(Error.NotFound("x", "x"));
            });

        await new LocalInstanceQueryGateway(_scopeFactory).GetInstanceDataAsync(
            new GetInstanceDataInput { Domain = "core", Workflow = "target", Instance = "key-1" });

        _readRanInsideIsolatedUnitOfWork.ShouldBeTrue();
    }

    [Fact]
    public async Task DirectTriggerKeyLookup_RunsInsideItsOwnUnitOfWork()
    {
        _repository.FindByIdentifierAsReadOnlyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                RecordIsolation();
                return Task.FromResult<Instance?>(null);
            });

        await new LocalInstanceIdResolver(_scopeFactory).ResolveIdAsync("core", "target", "key-1");

        _readRanInsideIsolatedUnitOfWork.ShouldBeTrue();
    }

    [Fact]
    public async Task RelatedInstanceRead_RunsInsideItsOwnUnitOfWork()
    {
        _relatedService.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                RecordIsolation();
                return Result<RelatedInstanceSnapshot?>.Ok(null);
            });

        await new LocalRelatedInstanceReader(_scopeFactory).ReadAsync(
            new RelatedInstanceRef(Guid.NewGuid(), "core", "target", null));

        _readRanInsideIsolatedUnitOfWork.ShouldBeTrue();
    }

    [Fact]
    public async Task HumanTaskLeafHop_RunsInsideItsOwnUnitOfWork()
    {
        _leafResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<HumanTaskLeafRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                RecordIsolation();
                return Result<IReadOnlyList<HumanTaskLeafResult>>.Ok([]);
            });

        await new LocalHumanTaskLeafGateway(_scopeFactory).ResolveAsync("core", "target", new HumanTaskLeafRequest());

        _readRanInsideIsolatedUnitOfWork.ShouldBeTrue();
    }

    /// <summary>
    /// The production shape: same-order tasks reading one flow concurrently. Each read must begin its own
    /// unit of work — one shared unit of work means one shared DbContext for the schema.
    /// </summary>
    [Fact]
    public async Task ConcurrentReadsOfOneFlow_EachBeginsItsOwnUnitOfWork()
    {
        var gate = new TaskCompletionSource();
        var inside = 0;
        _queryService.GetInstanceListAsync(Arg.Any<GetInstanceListInput>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (Interlocked.Increment(ref inside) == 4) gate.SetResult();
                await gate.Task;
                return Result<InstanceListWithGroupsResponse<GetInstanceOutput>>.Ok(new());
            });

        var gateway = new LocalInstanceQueryGateway(_scopeFactory);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => gateway.GetInstanceListAsync(
            new GetInstanceListInput { Domain = "core", Workflow = "target" })));

        _begun.Count.ShouldBe(4);
        _begun.ShouldAllBe(o => o.Scope == UnitOfWorkScopeOption.RequiresNew && !o.IsTransactional);
    }
}
