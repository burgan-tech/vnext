using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.DistributedLock;
using BBT.Aether.Results;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schedule;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Schedule;

/// <summary>
/// Covers what the schedule service contributes on top of the start path: the tick-derived key, the
/// query-string seed data, and the domain guard.
/// </summary>
public sealed class InstanceScheduleAppServiceTests
{
    private readonly IInstanceCommandAppService _commandAppService =
        Substitute.For<IInstanceCommandAppService>();
    private readonly IRuntimeInfoProvider _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IDistributedLockService _lockService = Substitute.For<IDistributedLockService>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();

    public InstanceScheduleAppServiceTests()
    {
        _clock.UtcNow.Returns(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _runtimeInfoProvider.IsDomainMatch(Arg.Any<string>()).Returns(true);
        _runtimeInfoProvider.Domain.Returns("morph-touch");
        // Lock granted by default; the contended case has its own test.
        _lockService
            .TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IDistributedLockHandle>());
        _commandAppService
            .StartAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Ok(new StartInstanceOutput { Id = Guid.NewGuid() }));
    }

    private InstanceScheduleAppService CreateSut() => new(
        _commandAppService, _runtimeInfoProvider, _instanceRepository, _lockService, _clock,
        NullLogger<InstanceScheduleAppService>.Instance);

    private static ScheduledStartInput Tick(
        string? scheduleId = "daily",
        string? readTimeUtc = "2026-10-05 07:25:42.93172757 +0000 UTC",
        Dictionary<string, string>? attributes = null,
        string? version = null) => new()
    {
        Domain = "morph-touch",
        Workflow = "rezervation",
        ScheduleId = scheduleId,
        Version = version,
        Attributes = attributes ?? new Dictionary<string, string>(),
        Headers = new Dictionary<string, string?>
        {
            [ScheduleTickKey.ReadTimeUtcHeader] = readTimeUtc
        }
    };

    /// <summary>
    /// The multi-replica guarantee, end to end through the service: two calls carrying the same tick
    /// must reach the start path with the same key, because that key is what the start path's
    /// idempotency uses to collapse them.
    /// </summary>
    [Fact]
    public async Task Same_tick_from_two_replicas_starts_with_the_same_instance_key()
    {
        var keys = new List<string?>();
        await _commandAppService.StartAsync(
            Arg.Do<StartInstanceInput>(i => keys.Add(i.Instance.Key)), Arg.Any<CancellationToken>());

        var sut = CreateSut();
        await sut.StartAsync(Tick(readTimeUtc: "2026-10-05 07:25:42.93172757 +0000 UTC"));
        await sut.StartAsync(Tick(readTimeUtc: "2026-10-05 07:25:42.00412 +0000 UTC"));

        keys.Count.ShouldBe(2);
        keys[0].ShouldBe(keys[1]);
        keys[0].ShouldBe("daily-20261005T072542Z");
    }

    /// <summary>Query parameters become the new instance's initial data, as strings.</summary>
    [Fact]
    public async Task Query_attributes_become_the_instance_body()
    {
        StartInstanceInput? captured = null;
        await _commandAppService.StartAsync(
            Arg.Do<StartInstanceInput>(i => captured = i), Arg.Any<CancellationToken>());

        await CreateSut().StartAsync(Tick(attributes: new Dictionary<string, string>
        {
            ["branchCode"] = "99999",
            ["reportType"] = "daily"
        }));

        captured.ShouldNotBeNull();
        captured!.Instance.Attributes.ShouldNotBeNull();

        var body = captured.Instance.Attributes!.Value;
        body.GetProperty("branchCode").GetString().ShouldBe("99999");
        body.GetProperty("reportType").GetString().ShouldBe("daily");
    }

    /// <summary>
    /// A tick with no query parameters starts the instance with NO body rather than an empty object —
    /// an empty object is a value a strict start schema can reject, and a bodyless tick is the norm.
    /// </summary>
    [Fact]
    public async Task Tick_without_query_parameters_starts_with_no_body()
    {
        StartInstanceInput? captured = null;
        await _commandAppService.StartAsync(
            Arg.Do<StartInstanceInput>(i => captured = i), Arg.Any<CancellationToken>());

        await CreateSut().StartAsync(Tick());

        captured!.Instance.Attributes.ShouldBeNull();
    }

    /// <summary>
    /// A scheduler wants a fast acknowledgement, and Dapr's cron binding discards the response, so the
    /// start must not block by default.
    /// </summary>
    [Fact]
    public async Task Start_is_asynchronous_by_default()
    {
        StartInstanceInput? captured = null;
        await _commandAppService.StartAsync(
            Arg.Do<StartInstanceInput>(i => captured = i), Arg.Any<CancellationToken>());

        await CreateSut().StartAsync(Tick());

        captured!.Sync.ShouldBeFalse();
        captured.StrictIdempotency.ShouldBeFalse(
            "key idempotency is what collapses replicas; strict mode would turn the duplicates into 409s");
    }

    /// <summary>
    /// A component pointed at a runtime serving another domain must not throw: nothing retries a cron
    /// tick, and an unhandled exception every tick buries the real signal.
    /// </summary>
    [Fact]
    public async Task Domain_mismatch_fails_as_a_result_rather_than_throwing()
    {
        _runtimeInfoProvider.IsDomainMatch("morph-touch").Returns(false);

        var result = await CreateSut().StartAsync(Tick());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("ScheduledStartDomainMismatch");
        await _commandAppService.DidNotReceive()
            .StartAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The tick headers must reach the start path — they carry trace context too.</summary>
    [Fact]
    public async Task Headers_are_forwarded_to_the_start_path()
    {
        StartInstanceInput? captured = null;
        await _commandAppService.StartAsync(
            Arg.Do<StartInstanceInput>(i => captured = i), Arg.Any<CancellationToken>());

        await CreateSut().StartAsync(Tick());

        captured!.Headers.ShouldContainKey(ScheduleTickKey.ReadTimeUtcHeader);
    }

    /// <summary>
    /// The guarantee this feature rests on. The start path's key idempotency is a check-then-insert
    /// over a NON-unique index, so simultaneous callers all pass the probe before any commits —
    /// measured on a live runtime, ten parallel calls carrying one tick produced nine instances.
    /// Replicas fire simultaneously by nature, so the per-tick lock is what makes one tick mean one
    /// instance.
    /// </summary>
    [Fact]
    public async Task Tick_is_claimed_under_a_key_derived_from_the_instance_key()
    {
        string? lockKey = null;
        _lockService
            .TryAcquireLockAsync(
                Arg.Do<string>(k => lockKey = k), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IDistributedLockHandle>());

        await CreateSut().StartAsync(Tick());

        lockKey.ShouldBe("vnext:schedule:morph-touch:rezervation:daily-20261005T072542Z");
    }

    /// <summary>
    /// A replica that loses the race must not start a second instance, and must not report failure
    /// either: the tick IS being handled, just by somebody else.
    /// </summary>
    [Fact]
    public async Task Losing_the_tick_claim_starts_nothing_and_still_succeeds()
    {
        _lockService
            .TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IDistributedLockHandle?)null);

        var result = await CreateSut().StartAsync(Tick());

        result.IsSuccess.ShouldBeTrue();
        await _commandAppService.DidNotReceive()
            .StartAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Different ticks must not contend with each other.</summary>
    [Fact]
    public async Task Different_ticks_take_different_claims()
    {
        var keys = new List<string>();
        _lockService
            .TryAcquireLockAsync(Arg.Do<string>(keys.Add), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IDistributedLockHandle>());

        var sut = CreateSut();
        await sut.StartAsync(Tick(readTimeUtc: "2026-10-05 07:25:42.1 +0000 UTC"));
        await sut.StartAsync(Tick(readTimeUtc: "2026-10-05 07:25:43.1 +0000 UTC"));

        keys.Count.ShouldBe(2);
        keys[0].ShouldNotBe(keys[1]);
    }

    /// <summary>
    /// The lock must be RELEASED. Its only job is to serialise simultaneous replicas; holding it after
    /// the start would leak one row per tick into <c>sys_queues.DistributedLocks</c> forever, because
    /// a tick key never recurs and the table's only delete is the release.
    /// </summary>
    [Fact]
    public async Task Tick_lock_is_released()
    {
        var handle = Substitute.For<IDistributedLockHandle>();
        _lockService
            .TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(handle);

        await CreateSut().StartAsync(Tick());

        await handle.Received().DisposeAsync();
    }

    /// <summary>The lock is released even when the start is refused, so a failure cannot leak a row.</summary>
    [Fact]
    public async Task Tick_lock_is_released_even_when_the_start_fails()
    {
        var handle = Substitute.For<IDistributedLockHandle>();
        _lockService
            .TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(handle);
        _commandAppService
            .StartAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Fail(Error.Validation("X", "schema rejected")));

        var result = await CreateSut().StartAsync(Tick());

        result.IsSuccess.ShouldBeFalse();
        await handle.Received().DisposeAsync();
    }

    /// <summary>
    /// A straggler arriving after the winner released must not start a second instance. Serialising
    /// alone cannot stop it: the start path treats a COMPLETED instance as a free key, so the guard
    /// has to be a status-agnostic probe. Measured before this probe existed: two instances.
    /// </summary>
    [Fact]
    public async Task Straggler_finding_an_existing_instance_starts_nothing()
    {
        _instanceRepository
            .FindByIdentifierSlimAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Instance.Create(Guid.NewGuid(), "morph-touch", "rezervation", "1.0.0"));

        var result = await CreateSut().StartAsync(Tick());

        result.IsSuccess.ShouldBeTrue();
        await _commandAppService.DidNotReceive()
            .StartAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>());
    }

    /// <summary>An explicit version pins the workflow the schedule starts.</summary>
    [Fact]
    public async Task Version_is_passed_through_to_the_start()
    {
        StartInstanceInput? captured = null;
        await _commandAppService.StartAsync(
            Arg.Do<StartInstanceInput>(i => captured = i), Arg.Any<CancellationToken>());

        await CreateSut().StartAsync(Tick(version: "1.2.0"));

        captured!.Version.ShouldBe("1.2.0");
    }

    /// <summary>Omitting it keeps the latest-version default a schedule normally wants.</summary>
    [Fact]
    public async Task Version_defaults_to_latest_when_absent()
    {
        StartInstanceInput? captured = null;
        await _commandAppService.StartAsync(
            Arg.Do<StartInstanceInput>(i => captured = i), Arg.Any<CancellationToken>());

        await CreateSut().StartAsync(Tick());

        captured!.Version.ShouldBeNull();
    }

    /// <summary>
    /// The claim is taken BEFORE the start, not around it. A sync start (or an `executionType: S`
    /// flow, which overrides the caller's request) runs the whole pipeline — tasks, HTTP calls,
    /// subflow starts — and the repo's locking rule forbids holding a lock across that.
    /// </summary>
    [Fact]
    public async Task Tick_is_claimed_before_the_start_is_attempted()
    {
        var order = new List<string>();
        _lockService
            .TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("claim"); return Substitute.For<IDistributedLockHandle>(); });
        _commandAppService
            .When(x => x.StartAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("start"));

        await CreateSut().StartAsync(Tick());

        order.ShouldBe(["claim", "start"]);
    }

    /// <summary>A refused start must surface its error unchanged for the mapper to classify.</summary>
    [Fact]
    public async Task Start_failure_is_propagated_with_its_error()
    {
        _commandAppService
            .StartAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Fail(Error.Validation("App:900002", "schema rejected")));

        var result = await CreateSut().StartAsync(Tick());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("App:900002");
    }
}
