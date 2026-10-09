using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
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

    public InstanceScheduleAppServiceTests()
    {
        _clock.UtcNow.Returns(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _runtimeInfoProvider.IsDomainMatch(Arg.Any<string>()).Returns(true);
        _runtimeInfoProvider.Domain.Returns("morph-touch");
        _commandAppService
            .StartAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Ok(new StartInstanceOutput { Id = Guid.NewGuid() }));
    }

    private InstanceScheduleAppService CreateSut() => new(
        _commandAppService, _runtimeInfoProvider, _clock,
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
