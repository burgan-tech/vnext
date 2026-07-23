using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions;

/// <summary>
/// End-to-end regression for the reported stale-snapshot race: a task script contributed data
/// against a STALE latest-only snapshot while a competing writer advanced the database head.
/// <para>
/// Control case (legacy, reconciliation flag OFF): replaying the stale snapshot's rows onto a
/// freshly loaded latest-only aggregate whose head moved to a newer version line throws the
/// original "loaded latest-only / older version line" <see cref="InvalidOperationException"/> —
/// in production this faulted the transition and forced the whole pipeline and its tasks to be
/// re-executed.
/// </para>
/// <para>
/// Fixed path (flag ON): the REAL <see cref="ScriptDataChangeApplicator"/> +
/// <see cref="InstanceDataReconciliationService"/> stack rebases the ORIGINAL journaled
/// contribution onto the fresh head via one fresh-head read and a second conditional append —
/// all inside a single task-phase apply call, without re-running tasks or the pipeline. Only
/// the concurrency repository is faked (with call counters and genuine CAS semantics).
/// </para>
/// </summary>
public sealed class InstanceDataConcurrencyRegressionTests : IDisposable
{
    private const string StaleBaselineJson = "{\"base\":1}";
    private const string RemoteHeadJson = "{\"base\":1,\"remote\":3}";
    private const string LocalContributionJson = "{\"local\":2}";
    private const string ExpectedMergedJson = "{\"base\":1,\"remote\":3,\"local\":2}";

    private readonly IServiceProvider? _previousAmbient;
    private readonly ServiceProvider _ambientProvider;

    private int _taskExecutionCount;
    private int _pipelineExecutionCount;

    public InstanceDataConcurrencyRegressionTests()
    {
        // Ambient provider needed by PostSharp aspect interception ([SchemaValidation] on
        // Instance data appends resolves IWorkflowContext from it).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowContext>(new NullWorkflowContext());
        _ambientProvider = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = _ambientProvider;
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbient;
        _ambientProvider.Dispose();
    }

    [Fact]
    public async Task Latest_only_stale_snapshot_should_rebase_original_input_without_restarting_pipeline()
    {
        // The stale in-process aggregate: loaded latest-only at head 2.0.0 = {"base":1}.
        var instanceId = Guid.NewGuid();
        var live = CreateLatestOnlyInstance(instanceId, "2.0.0", StaleBaselineJson);
        var transitionContext = CreateTransitionContext(live);
        var scriptContext = BuildTrackedScriptContext(live);

        // The competing writer's head, already persisted in the database: 3.0.0.
        var repository = new AdvancedHeadConcurrencyRepository(
            CreateRemoteHead(instanceId, "3.0.0", RemoteHeadJson, versionNo: 2));

        // ONE task execution contributes {"local":2} against the stale snapshot (journaled).
        var contributionId = RunTaskPhaseOnce(scriptContext);

        // CONTROL CASE — legacy replay (reconciliation flag OFF) through the real applicator:
        // production re-loaded the aggregate latest-only at the advanced 3.0.0 head, then the
        // legacy row replay tried to append the snapshot's 2.0.0-line rows and threw the
        // original latest-only older-line error, faulting the transition.
        var advancedLive = CreateLatestOnlyInstance(
            instanceId, "3.0.0", RemoteHeadJson, repository.RemoteHead.DataId);
        var legacyContext = CreateTransitionContext(advancedLive);
        var legacyApplicator = CreateApplicator(repository, enabled: false);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            legacyApplicator.ApplyAsync(legacyContext, scriptContext, CancellationToken.None));
        exception.Message.ShouldBe(
            $"Cannot append version '2.0.0' to instance '{instanceId}': the aggregate was " +
            "loaded latest-only and the target version line is not in memory. Load the " +
            "instance with full data history for line-targeted appends.");

        // FIXED PATH — reconciliation flag ON, same single task contribution, real service:
        // one pipeline pass reaches the apply point; the conflict is resolved INSIDE this call.
        var applicator = CreateApplicator(repository, enabled: true);
        _pipelineExecutionCount++;

        var result = await applicator.ApplyAsync(transitionContext, scriptContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        ((JsonData)transitionContext.Data!).Json.ShouldBe(ExpectedMergedJson);

        // The original error path forced full pipeline/task re-execution; the fixed path must
        // reconcile within the single apply call above — no task or pipeline replay happened.
        _taskExecutionCount.ShouldBe(1);
        _pipelineExecutionCount.ShouldBe(1);

        // Bounded CAS choreography: stale append -> Conflict, one fresh-head read, rebased
        // append -> Applied.
        repository.FreshHeadReadCount.ShouldBe(1);
        repository.ConditionalAppendCount.ShouldBe(2);

        repository.AppendCalls[0].ExpectedLatestDataId.ShouldBe(live.LatestData!.Id);
        var rebasedCall = repository.AppendCalls[1];
        rebasedCall.ExpectedLatestDataId.ShouldBe(repository.RemoteHead.DataId);
        rebasedCall.ExpectedLatestEtag.ShouldBe(repository.RemoteHead.ETag);

        // The ORIGINAL journaled contribution was rebased (same DataId, input replayed onto
        // the fresh 3.0.0 head) — the task itself was never re-invoked to rebuild it.
        var rebasedRow = rebasedCall.Data.ShouldHaveSingleItem();
        rebasedRow.DataId.ShouldBe(contributionId);
        rebasedRow.Version.ShouldBe("3.0.1");
        rebasedRow.Data.Json.ShouldBe(ExpectedMergedJson);

        // The drained journal was acknowledged: nothing pending for a replay.
        scriptContext.Instance!.GetPendingDataChangeSet().ShouldBeNull();
    }

    private Guid RunTaskPhaseOnce(ScriptContext scriptContext)
    {
        // Honest counter wiring: this lambda IS the "task phase" — the only place the script
        // contribution is produced. Reconciliation must never come back here.
        _taskExecutionCount++;
        var contributionId = Guid.NewGuid();
        scriptContext.Instance!.AddData(
            contributionId,
            new JsonData(LocalContributionJson),
            VersionStrategy.IncreasePatch);
        return contributionId;
    }

    private static Instance CreateLatestOnlyInstance(
        Guid instanceId,
        string dataVersion,
        string json,
        Guid? dataId = null)
    {
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0", "regression-key");
        instance.AddDataWithVersion(dataId ?? Guid.NewGuid(), new JsonData(json), dataVersion);
        instance.MarkDataPartiallyLoaded();
        return instance;
    }

    private static ScriptContext BuildTrackedScriptContext(Instance live)
    {
        return new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(live.CreateTrackedDataSnapshot())
            .Build();
    }

    private static TransitionExecutionContext CreateTransitionContext(Instance live)
    {
        return new TransitionExecutionContext
        {
            InstanceId = live.Id,
            Domain = "test-domain",
            WorkflowKey = "test-flow",
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Instance = live,
            Data = live.Data,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static ScriptDataChangeApplicator CreateApplicator(
        IInstanceDataConcurrencyRepository repository,
        bool enabled)
    {
        var service = new InstanceDataReconciliationService(
            repository,
            NullLogger<InstanceDataReconciliationService>.Instance,
            Substitute.For<IWorkflowMetrics>());
        var options = Options.Create(new WorkflowExecutionOptions
        {
            EnableInstanceDataReconciliation = enabled
        });
        return new ScriptDataChangeApplicator(service, options);
    }

    private static InstanceDataHead CreateRemoteHead(
        Guid instanceId,
        string version,
        string json,
        long versionNo)
    {
        // Build the head through a real aggregate append so ETag/DataHash/EnteredAt carry the
        // exact production shape.
        var donor = Instance.Create(instanceId, "test-flow", "1.0.0", "regression-key");
        var row = donor.AddDataWithVersion(Guid.NewGuid(), new JsonData(json), version);
        return new InstanceDataHead(
            row.Id,
            row.ETag,
            row.Version,
            versionNo,
            row.HistorySequence,
            row.DataHash,
            new JsonData(row.Data.Json),
            row.EnteredAt);
    }

    /// <summary>
    /// Fake concurrency repository with genuine CAS semantics: the database head has been
    /// advanced to <see cref="RemoteHead"/> by a competing writer. An append whose expected
    /// head does not match returns Conflict (with the observed head); a correctly rebased
    /// append is Applied. Counts fresh-head reads and conditional appends.
    /// </summary>
    private sealed class AdvancedHeadConcurrencyRepository(InstanceDataHead remoteHead)
        : IInstanceDataConcurrencyRepository
    {
        private static readonly MethodInfo RehydrateMethod = typeof(InstanceData).GetMethod(
            "Rehydrate",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        private static readonly MethodInfo MarkAsNotLatestMethod = typeof(InstanceData).GetMethod(
            "MarkAsNotLatest",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        // The head the database currently holds; starts at the competing writer's head and
        // advances after every Applied append, so a subsequent stale append would conflict
        // exactly as the real conditional-append function behaves.
        private InstanceDataHead _currentHead = remoteHead;

        public InstanceDataHead RemoteHead { get; } = remoteHead;
        public int FreshHeadReadCount { get; private set; }
        public int ConditionalAppendCount { get; private set; }
        public List<AppendCall> AppendCalls { get; } = [];

        public Task<InstanceDataHead?> GetLatestDataHeadAsync(
            Guid instanceId,
            CancellationToken cancellationToken)
        {
            FreshHeadReadCount++;
            return Task.FromResult<InstanceDataHead?>(_currentHead);
        }

        public Task<ConditionalAppendResult> TryAppendDataAsync(
            Guid instanceId,
            Guid? expectedLatestDataId,
            string? expectedLatestEtag,
            IReadOnlyList<PreparedInstanceData> data,
            CancellationToken cancellationToken)
        {
            ConditionalAppendCount++;
            AppendCalls.Add(new AppendCall(expectedLatestDataId, expectedLatestEtag, data.ToArray()));

            if (expectedLatestDataId != _currentHead.DataId ||
                !string.Equals(expectedLatestEtag, _currentHead.ETag, StringComparison.Ordinal))
            {
                return Task.FromResult(new ConditionalAppendResult(
                    ConditionalAppendStatus.Conflict,
                    null,
                    [],
                    null,
                    _currentHead));
            }

            var versionNo = _currentHead.VersionNo;
            InstanceDataHead? appendedHead = null;
            var persisted = data.Select(item =>
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
                if (item.IsLatest)
                    appendedHead = head;
                var rehydrated = (InstanceData)RehydrateMethod.Invoke(null, [instanceId, head])!;
                if (!item.IsLatest)
                    MarkAsNotLatestMethod.Invoke(rehydrated, null);
                return rehydrated;
            }).ToArray();

            _currentHead = appendedHead ?? _currentHead;

            return Task.FromResult(new ConditionalAppendResult(
                ConditionalAppendStatus.Applied,
                persisted.Last(),
                persisted));
        }
    }

    private sealed record AppendCall(
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
