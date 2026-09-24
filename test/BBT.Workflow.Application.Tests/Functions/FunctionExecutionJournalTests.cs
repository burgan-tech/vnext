using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions;
using BBT.Workflow.Metrics;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Functions;

/// <summary>
/// Unit tests for <see cref="FunctionExecutionJournal"/> — that it builds the journal entity from the
/// observed facts and that a write failure is swallowed (journaling must never fail the function).
/// </summary>
public class FunctionExecutionJournalTests
{
    private readonly IFunctionExecutionRepository _repository = Substitute.For<IFunctionExecutionRepository>();
    private readonly IUnitOfWorkManager _uowManager = Substitute.For<IUnitOfWorkManager>();
    private readonly IGuidGenerator _guidGenerator = Substitute.For<IGuidGenerator>();
    private readonly FunctionExecutionJournal _journal;

    public FunctionExecutionJournalTests()
    {
        var uow = Substitute.For<IUnitOfWork>();
        uow.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(uow);

        _journal = new FunctionExecutionJournal(
            _repository, _uowManager, _guidGenerator,
            Substitute.For<ILogger<FunctionExecutionJournal>>());
    }

    [Fact]
    public async Task RecordAsync_BuildsEntityFromRecordAndInserts()
    {
        var id = Guid.NewGuid();
        _guidGenerator.Create().Returns(id);
        var instanceId = Guid.NewGuid();
        var invokedAt = DateTime.UtcNow.AddSeconds(-1);

        FunctionExecution? captured = null;
        await _repository.InsertAsync(Arg.Do<FunctionExecution>(e => captured = e), Arg.Any<CancellationToken>());

        await _journal.RecordAsync(new FunctionExecutionRecord(
            Domain: "sample",
            FunctionKey: "get-report",
            FunctionVersion: "2.0.0",
            Scope: TaskScope.Instance,
            Workflow: "north-star",
            InstanceId: instanceId,
            InvokedAt: invokedAt,
            DurationMs: 42.5,
            Succeeded: false,
            StatusCode: null,
            ErrorCode: "Task:Http:500",
            FromCache: true));

        await _repository.Received(1).InsertAsync(Arg.Any<FunctionExecution>(), Arg.Any<CancellationToken>());
        captured.ShouldNotBeNull();
        captured!.Id.ShouldBe(id);
        captured.Domain.ShouldBe("sample");
        captured.FunctionKey.ShouldBe("get-report");
        captured.FunctionVersion.ShouldBe("2.0.0");
        captured.Scope.ShouldBe("I");
        captured.Workflow.ShouldBe("north-star");
        captured.InstanceId.ShouldBe(instanceId);
        captured.InvokedAt.ShouldBe(invokedAt);
        captured.DurationMs.ShouldBe(42.5);
        captured.Succeeded.ShouldBeFalse();
        captured.StatusCode.ShouldBeNull();
        captured.ErrorCode.ShouldBe("Task:Http:500");
        captured.FromCache.ShouldBeTrue();
    }

    [Fact]
    public async Task RecordAsync_WhenWriteFails_SwallowsSoTheFunctionIsUnaffected()
    {
        _guidGenerator.Create().Returns(Guid.NewGuid());
        _repository
            .InsertAsync(Arg.Any<FunctionExecution>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));

        // Must not throw.
        await Should.NotThrowAsync(() => _journal.RecordAsync(new FunctionExecutionRecord(
            "sample", "get-report", "1.0.0", TaskScope.Domain, null, null,
            DateTime.UtcNow, 10, true, 200, null, false)));
    }
}
