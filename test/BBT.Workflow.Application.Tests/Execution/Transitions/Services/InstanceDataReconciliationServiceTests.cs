using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Services;

public sealed class InstanceDataReconciliationServiceTests
{
    public InstanceDataReconciliationServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowContext>(new NullWorkflowContext());
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Fast_path_should_append_once_without_reading_fresh_head()
    {
        var fixture = ReconciliationFixture.Create();
        fixture.Repository.EnqueueAppend(fixture.Repository.Applied);

        var result = await fixture.Service.ApplyAsync(
            fixture.Live,
            fixture.ChangeSet,
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.AttemptCount.ShouldBe(1);
        result.Value.WasRebased.ShouldBeFalse();
        fixture.Repository.AppendCalls.Count.ShouldBe(1);
        fixture.Repository.FreshReadCount.ShouldBe(0);
        fixture.Repository.AppendCalls[0].ExpectedLatestDataId.ShouldBe(fixture.ChangeSet.Baseline!.DataId);
        fixture.Repository.AppendCalls[0].ExpectedLatestEtag.ShouldBe(fixture.ChangeSet.Baseline.ETag);
    }

    [Fact]
    public async Task Instance_id_mismatch_should_throw_without_repository_calls()
    {
        var fixture = ReconciliationFixture.Create();
        var mismatched = fixture.ChangeSet with { InstanceId = Guid.NewGuid() };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ApplyAsync(fixture.Live, mismatched, CancellationToken.None));

        exception.Message.ShouldBe(
            $"Instance data change set '{mismatched.InstanceId}' does not belong to instance '{fixture.Live.Id}'.");
        fixture.Repository.AppendCalls.Count.ShouldBe(0);
        fixture.Repository.FreshReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task Baseline_mismatch_should_throw_without_repository_calls()
    {
        var fixture = ReconciliationFixture.Create();
        var mismatched = fixture.ChangeSet with
        {
            Baseline = fixture.ChangeSet.Baseline! with { ETag = "different-etag" }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ApplyAsync(fixture.Live, mismatched, CancellationToken.None));

        exception.Message.ShouldBe(
            "Instance data reconciliation baseline does not match the supplied instance latest data.");
        fixture.Repository.AppendCalls.Count.ShouldBe(0);
        fixture.Repository.FreshReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task Null_baseline_with_live_data_should_throw_without_repository_calls()
    {
        var fixture = ReconciliationFixture.CreateWithoutBaseline("{\"local\":2}");
        fixture.Live.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"unexpected\":1}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ApplyAsync(fixture.Live, fixture.ChangeSet, CancellationToken.None));

        exception.Message.ShouldBe(
            "Instance data reconciliation expected the supplied instance to have no latest data.");
        fixture.Repository.AppendCalls.Count.ShouldBe(0);
        fixture.Repository.FreshReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task One_conflict_should_refresh_and_replay_original_contribution()
    {
        var fixture = ReconciliationFixture.Create(localInput: "{\"local\":2}");
        var remoteHead = fixture.Head("{\"base\":1,\"remote\":1}");
        fixture.Repository.EnqueueAppend(
            _ => Conflict(),
            fixture.Repository.Applied);
        fixture.Repository.EnqueueHead(remoteHead);

        var result = await fixture.Service.ApplyAsync(
            fixture.Live,
            fixture.ChangeSet,
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.AttemptCount.ShouldBe(2);
        result.Value.WasRebased.ShouldBeTrue();
        result.Value.LatestData.Data.Json.ShouldBe("{\"base\":1,\"remote\":1,\"local\":2}");
        fixture.Repository.FreshReadCount.ShouldBe(1);

        var rebasedCall = fixture.Repository.AppendCalls[1];
        rebasedCall.ExpectedLatestDataId.ShouldBe(remoteHead.DataId);
        rebasedCall.ExpectedLatestEtag.ShouldBe(remoteHead.ETag);
        rebasedCall.Data.Count.ShouldBe(1);
        rebasedCall.Data[0].DataId.ShouldBe(fixture.ContributionIds[0]);
        rebasedCall.Data[0].Version.ShouldBe("1.0.2");
        rebasedCall.Data[0].Data.Json.ShouldBe("{\"base\":1,\"remote\":1,\"local\":2}");
    }

    [Fact]
    public async Task Stable_contribution_id_on_fresh_head_should_still_append_and_surface_repository_error()
    {
        var fixture = ReconciliationFixture.Create(localInput: "{\"local\":2}");
        var idempotencyError = Error.Conflict(
            "InstanceData:IdempotencyConflict",
            "The contribution ID already exists with different content.");
        fixture.Repository.EnqueueAppend(
            _ => Conflict(),
            _ => new ConditionalAppendResult(
                ConditionalAppendStatus.Conflict,
                null,
                [],
                idempotencyError));
        fixture.Repository.EnqueueHead(fixture.Head(
            "{\"remote\":1}",
            fixture.ContributionIds[0]));

        var result = await fixture.Service.ApplyAsync(
            fixture.Live,
            fixture.ChangeSet,
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(idempotencyError);
        fixture.Repository.AppendCalls.Count.ShouldBe(2);
        fixture.Repository.FreshReadCount.ShouldBe(1);
        var prepared = fixture.Repository.AppendCalls[1].Data.ShouldHaveSingleItem();
        prepared.DataId.ShouldBe(fixture.ContributionIds[0]);
        prepared.Data.Json.ShouldBe("{\"remote\":1,\"local\":2}");
    }

    [Fact]
    public async Task Conflict_followed_by_missing_fresh_head_should_replay_against_empty_data()
    {
        var fixture = ReconciliationFixture.Create(localInput: "{\"local\":2}");
        fixture.Repository.EnqueueAppend(
            _ => Conflict(),
            fixture.Repository.Applied);
        fixture.Repository.EnqueueHead((InstanceDataHead?)null);

        var result = await fixture.Service.ApplyAsync(
            fixture.Live,
            fixture.ChangeSet,
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.AttemptCount.ShouldBe(2);
        result.Value.WasRebased.ShouldBeTrue();
        fixture.Repository.FreshReadCount.ShouldBe(1);
        var retry = fixture.Repository.AppendCalls[1];
        retry.ExpectedLatestDataId.ShouldBeNull();
        retry.ExpectedLatestEtag.ShouldBeNull();
        retry.Data.ShouldHaveSingleItem().Data.Json.ShouldBe("{\"local\":2}");
        retry.Data[0].Version.ShouldBe(WorkflowConstants.DefaultVersion);
    }

    [Fact]
    public async Task Conflict_followed_by_replay_dedup_should_succeed_without_second_append()
    {
        var fixture = ReconciliationFixture.Create(localInput: "{\"same\":1}");
        var freshHead = fixture.Head("{\"same\":1}");
        fixture.Repository.EnqueueAppend(_ => Conflict());
        fixture.Repository.EnqueueHead(freshHead);

        var result = await fixture.Service.ApplyAsync(
            fixture.Live,
            fixture.ChangeSet,
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.AttemptCount.ShouldBe(2);
        result.Value.WasRebased.ShouldBeTrue();
        result.Value.LatestData.Id.ShouldBe(freshHead.DataId);
        fixture.Repository.AppendCalls.Count.ShouldBe(1);
        fixture.Repository.FreshReadCount.ShouldBe(1);
    }

    [Fact]
    public async Task Repository_no_change_should_succeed_without_retry()
    {
        var fixture = ReconciliationFixture.Create();
        fixture.Repository.EnqueueAppend(fixture.Repository.NoChange);

        var result = await fixture.Service.ApplyAsync(
            fixture.Live,
            fixture.ChangeSet,
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.AttemptCount.ShouldBe(1);
        result.Value.WasRebased.ShouldBeFalse();
        result.Value.AppendedData.ShouldBeEmpty();
        fixture.Repository.AppendCalls.Count.ShouldBe(1);
        fixture.Repository.FreshReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_repository_status_should_throw_without_retry()
    {
        var fixture = ReconciliationFixture.Create();
        const ConditionalAppendStatus unknownStatus = (ConditionalAppendStatus)999;
        fixture.Repository.EnqueueAppend(_ => new ConditionalAppendResult(
            unknownStatus,
            null,
            []));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ApplyAsync(fixture.Live, fixture.ChangeSet, CancellationToken.None));

        exception.Message.ShouldBe("Unsupported conditional append status '999'.");
        fixture.Repository.AppendCalls.Count.ShouldBe(1);
        fixture.Repository.FreshReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task Fifth_conflict_should_return_explicit_error_without_attempt_six()
    {
        var fixture = ReconciliationFixture.Create();
        fixture.Repository.EnqueueAppend(Enumerable.Repeat<Func<AppendCall, ConditionalAppendResult>>(
            _ => Conflict(),
            InstanceDataReconciliationService.MaxAttempts).ToArray());
        fixture.Repository.EnqueueHead(Enumerable.Range(0, InstanceDataReconciliationService.MaxAttempts - 1)
            .Select(_ => fixture.Head("{\"base\":1,\"remote\":1}"))
            .ToArray());

        var result = await fixture.Service.ApplyAsync(
            fixture.Live,
            fixture.ChangeSet,
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceDataConcurrencyConflict);
        result.Error.Message.ShouldBe(
            $"Instance data changed concurrently and could not be reconciled after {InstanceDataReconciliationService.MaxAttempts} attempts.");
        result.Error.Target.ShouldBe(fixture.Live.Id.ToString());
        fixture.Repository.AppendCalls.Count.ShouldBe(5);
        fixture.Repository.FreshReadCount.ShouldBe(4);
    }

    [Fact]
    public async Task Null_baseline_should_conditionally_append_the_first_row_against_no_expected_head()
    {
        var fixture = ReconciliationFixture.CreateWithoutBaseline("{\"first\":1}");
        fixture.Repository.EnqueueAppend(fixture.Repository.Applied);

        var result = await fixture.Service.ApplyAsync(
            fixture.Live,
            fixture.ChangeSet,
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        fixture.Repository.FreshReadCount.ShouldBe(0);
        var call = fixture.Repository.AppendCalls.ShouldHaveSingleItem();
        call.ExpectedLatestDataId.ShouldBeNull();
        call.ExpectedLatestEtag.ShouldBeNull();
        call.Data.ShouldHaveSingleItem().DataId.ShouldBe(fixture.ContributionIds[0]);
        call.Data[0].Version.ShouldBe(WorkflowConstants.DefaultVersion);
        call.Data[0].Data.Json.ShouldBe("{\"first\":1}");
    }

    [Fact]
    public async Task Repository_error_should_be_returned_without_retry_or_fresh_read()
    {
        var fixture = ReconciliationFixture.Create();
        var repositoryError = Error.Failure("InstanceData:WriteFailed", "Database write failed.");
        fixture.Repository.EnqueueAppend(_ => new ConditionalAppendResult(
            ConditionalAppendStatus.Conflict,
            null,
            [],
            repositoryError));

        var result = await fixture.Service.ApplyAsync(
            fixture.Live,
            fixture.ChangeSet,
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(repositoryError);
        fixture.Repository.AppendCalls.Count.ShouldBe(1);
        fixture.Repository.FreshReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task Multiple_contributions_should_replay_in_journal_order_with_existing_AddData_semantics()
    {
        var fixture = ReconciliationFixture.Create(
            ("{\"first\":1}", VersionStrategy.IncreasePatch),
            ("{\"second\":2}", VersionStrategy.IncreaseMinor));
        fixture.Repository.EnqueueAppend(fixture.Repository.Applied);

        var result = await fixture.Service.ApplyAsync(
            fixture.Live,
            fixture.ChangeSet,
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var prepared = fixture.Repository.AppendCalls.ShouldHaveSingleItem().Data;
        prepared.Select(x => x.DataId).ShouldBe(fixture.ContributionIds);
        prepared.Select(x => x.Version).ShouldBe(["1.0.1", "1.1.0"]);
        prepared.Select(x => x.Data.Json).ShouldBe([
            "{\"base\":1,\"first\":1}",
            "{\"base\":1,\"first\":1,\"second\":2}"
        ]);
        result.Value!.LatestData.Id.ShouldBe(fixture.ContributionIds[1]);
        result.Value.LatestData.VersionNo.ShouldBeGreaterThan(0);
    }

    private static ConditionalAppendResult Conflict() =>
        new(ConditionalAppendStatus.Conflict, null, []);

    private sealed class ReconciliationFixture
    {
        private ReconciliationFixture(
            Instance live,
            InstanceDataChangeSet changeSet,
            IReadOnlyList<Guid> contributionIds)
        {
            Live = live;
            ChangeSet = changeSet;
            ContributionIds = contributionIds;
            Repository = new ScriptedInstanceDataRepository();
            Service = new InstanceDataReconciliationService(Repository);
        }

        public Instance Live { get; }
        public InstanceDataChangeSet ChangeSet { get; }
        public IReadOnlyList<Guid> ContributionIds { get; }
        public ScriptedInstanceDataRepository Repository { get; }
        public InstanceDataReconciliationService Service { get; }

        public static ReconciliationFixture Create(string localInput = "{\"local\":2}") =>
            Create((localInput, VersionStrategy.IncreasePatch));

        public static ReconciliationFixture Create(
            params (string Input, VersionStrategy Strategy)[] contributions)
        {
            var live = InstanceFactory.CreateDefault();
            live.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"base\":1}"));
            live.MarkDataPartiallyLoaded();
            return CreateFromLive(live, contributions);
        }

        public static ReconciliationFixture CreateWithoutBaseline(string localInput)
        {
            var live = InstanceFactory.CreateDefault();
            live.MarkDataPartiallyLoaded();
            return CreateFromLive(live, (localInput, VersionStrategy.IncreasePatch));
        }

        private static ReconciliationFixture CreateFromLive(
            Instance live,
            params (string Input, VersionStrategy Strategy)[] contributions)
        {
            var tracked = live.CreateTrackedDataSnapshot();
            var ids = new List<Guid>();
            foreach (var contribution in contributions)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                tracked.AddData(id, JsonData.CreateFrom(contribution.Input), contribution.Strategy);
            }

            return new ReconciliationFixture(
                live,
                tracked.GetPendingDataChangeSet()!,
                ids);
        }

        public InstanceDataHead Head(string json, Guid? dataId = null)
        {
            var data = new JsonData(json);
            var fingerprintSource = InstanceFactory.CreateDefault()
                .AddData(Guid.NewGuid(), data);
            return new InstanceDataHead(
                dataId ?? Guid.NewGuid(),
                $"etag-{Guid.NewGuid():N}",
                "1.0.1",
                41,
                1,
                fingerprintSource.DataHash,
                data,
                DateTime.UtcNow);
        }
    }

    private sealed class ScriptedInstanceDataRepository : IInstanceDataConcurrencyRepository
    {
        private static readonly MethodInfo RehydrateMethod = typeof(InstanceData).GetMethod(
            "Rehydrate",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        private static readonly MethodInfo MarkAsNotLatestMethod = typeof(InstanceData).GetMethod(
            "MarkAsNotLatest",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly Queue<Func<AppendCall, ConditionalAppendResult>> _appendResults = [];
        private readonly Queue<InstanceDataHead?> _heads = [];

        public List<AppendCall> AppendCalls { get; } = [];
        public int FreshReadCount { get; private set; }

        public void EnqueueAppend(params Func<AppendCall, ConditionalAppendResult>[] results)
        {
            foreach (var result in results)
                _appendResults.Enqueue(result);
        }

        public void EnqueueHead(params InstanceDataHead?[] heads)
        {
            foreach (var head in heads)
                _heads.Enqueue(head);
        }

        public Task<InstanceDataHead?> GetLatestDataHeadAsync(
            Guid instanceId,
            CancellationToken cancellationToken)
        {
            FreshReadCount++;
            return Task.FromResult(_heads.Dequeue());
        }

        public Task<ConditionalAppendResult> TryAppendDataAsync(
            Guid instanceId,
            Guid? expectedLatestDataId,
            string? expectedLatestEtag,
            IReadOnlyList<PreparedInstanceData> data,
            CancellationToken cancellationToken)
        {
            var call = new AppendCall(
                instanceId,
                expectedLatestDataId,
                expectedLatestEtag,
                data.ToArray());
            AppendCalls.Add(call);
            return Task.FromResult(_appendResults.Dequeue()(call));
        }

        public ConditionalAppendResult Applied(AppendCall call)
        {
            var versionNo = 100L;
            var persisted = call.Data.Select(item =>
            {
                var head = new InstanceDataHead(
                    item.DataId,
                    item.ETag,
                    item.Version,
                    ++versionNo,
                    item.HistorySequence,
                    item.DataHash,
                    new JsonData(item.Data.Json),
                    item.EnteredAt);
                var rehydrated = (InstanceData)RehydrateMethod.Invoke(null, [call.InstanceId, head])!;
                if (!item.IsLatest)
                    MarkAsNotLatestMethod.Invoke(rehydrated, null);
                return rehydrated;
            }).ToArray();

            return new ConditionalAppendResult(
                ConditionalAppendStatus.Applied,
                persisted.Last(),
                persisted);
        }

        public ConditionalAppendResult NoChange(AppendCall call)
        {
            var applied = Applied(call);
            return applied with
            {
                Status = ConditionalAppendStatus.NoChange,
                AppendedData = []
            };
        }
    }

    private sealed record AppendCall(
        Guid InstanceId,
        Guid? ExpectedLatestDataId,
        string? ExpectedLatestEtag,
        IReadOnlyList<PreparedInstanceData> Data);

    private sealed class NullWorkflowContext : IWorkflowContext
    {
        public Definitions.Workflow? Workflow => null;
        public bool HasWorkflow => false;

        public void SetWorkflow(Definitions.Workflow workflow)
        {
        }
    }
}
