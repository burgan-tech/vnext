using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Services;

/// <summary>
/// Unit tests for <see cref="ScriptDataChangeApplicator"/> — the single application point
/// for reconciled script data changes in the three task phases.
/// </summary>
public sealed class ScriptDataChangeApplicatorTests
{
    public ScriptDataChangeApplicatorTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowContext>(new NullWorkflowContext());
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Enabled_success_should_update_context_acknowledge_journal_then_apply_mutations()
    {
        var fixture = ApplicatorFixture.Create(enabled: true);
        fixture.ScriptContext.Mutations.SetStage("review");
        fixture.Reconciler.ApplyAsync(default!, default!, default)
            .ReturnsForAnyArgs(Result<InstanceDataReconciliationResult>.Ok(fixture.Success));

        var result = await fixture.Applicator.ApplyAsync(fixture.Transition, fixture.ScriptContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        ((JsonData)fixture.Transition.Data!).Json.ShouldBe(fixture.Success.LatestData.Data.Json);
        fixture.Transition.Instance.Stage.ShouldBe("review");
        fixture.ScriptContext.Instance!.GetPendingDataChangeSet().ShouldBeNull();
        await fixture.Reconciler.Received(1).ApplyAsync(
            fixture.Transition.Instance,
            Arg.Any<InstanceDataChangeSet>(),
            Arg.Any<CancellationToken>());

        // Tracked-aggregate row synchronization is owned by the repository append (mocked out
        // here), so the applicator must not have touched the aggregate's data list itself.
        fixture.Transition.Instance.DataList.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Enabled_rebase_already_applied_should_not_attach_detached_latest_row()
    {
        var fixture = ApplicatorFixture.Create(enabled: true);

        // Short-circuit shape from InstanceDataReconciliationService: a competing writer already
        // persisted the batch, so AppendedData is empty and LatestData is a DETACHED rehydrated
        // head whose Id does not exist in the tracked partially loaded aggregate.
        var detached = fixture.Success.LatestData;
        fixture.Reconciler.ApplyAsync(default!, default!, default)
            .ReturnsForAnyArgs(Result<InstanceDataReconciliationResult>.Ok(
                new InstanceDataReconciliationResult(detached, [], 2, true)));

        var result = await fixture.Applicator.ApplyAsync(fixture.Transition, fixture.ScriptContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        ((JsonData)fixture.Transition.Data!).Json.ShouldBe(detached.Data.Json);

        // The detached row must NOT be pushed into the tracked aggregate — EF would mark it
        // Added and re-insert an already persisted row (duplicate primary key).
        fixture.Transition.Instance.DataList.ShouldNotContain(x => x.Id == detached.Id);
        fixture.Transition.Instance.DataList.Count.ShouldBe(1);
        fixture.ScriptContext.Instance!.GetPendingDataChangeSet().ShouldBeNull();
    }

    [Fact]
    public async Task Enabled_with_null_script_instance_should_no_op_like_legacy()
    {
        var fixture = ApplicatorFixture.Create(enabled: true, withScriptInstance: false);
        fixture.ScriptContext.Mutations.SetStage("review");

        var result = await fixture.Applicator.ApplyAsync(fixture.Transition, fixture.ScriptContext, CancellationToken.None);

        // Legacy ApplyScriptContextChanges skips everything (rows AND mutations) when the
        // script instance is null; the flag-ON path must not silently differ.
        result.IsSuccess.ShouldBeTrue();
        fixture.Transition.Instance.Stage.ShouldNotBe("review");
        await fixture.Reconciler.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default);
    }

    [Fact]
    public async Task Enabled_failure_should_leave_journal_and_mutations_unapplied()
    {
        var fixture = ApplicatorFixture.Create(enabled: true);
        fixture.ScriptContext.Mutations.SetStage("review");
        fixture.Reconciler.ApplyAsync(default!, default!, default)
            .ReturnsForAnyArgs(Result<InstanceDataReconciliationResult>.Fail(
                WorkflowErrors.InstanceDataConcurrencyConflict(fixture.Transition.Instance.Id, 5)));

        var result = await fixture.Applicator.ApplyAsync(fixture.Transition, fixture.ScriptContext, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceDataConcurrencyConflict);
        fixture.ScriptContext.Instance!.GetPendingDataChangeSet().ShouldNotBeNull();
        fixture.Transition.Instance.Stage.ShouldNotBe("review");
    }

    [Fact]
    public async Task Enabled_without_pending_changes_should_apply_mutations_without_reconciler()
    {
        var fixture = ApplicatorFixture.Create(enabled: true, withPendingChanges: false);
        fixture.ScriptContext.Mutations.SetStage("review");

        var result = await fixture.Applicator.ApplyAsync(fixture.Transition, fixture.ScriptContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        fixture.Transition.Instance.Stage.ShouldBe("review");
        await fixture.Reconciler.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default);
    }

    [Fact]
    public async Task Disabled_flag_should_use_legacy_fail_fast_row_replay()
    {
        var fixture = ApplicatorFixture.Create(enabled: false);
        fixture.ScriptContext.Mutations.SetStage("review");

        var result = await fixture.Applicator.ApplyAsync(fixture.Transition, fixture.ScriptContext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await fixture.Reconciler.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default);

        // Legacy replay copies the script snapshot's new data row onto the live instance
        // and still applies mutations.
        fixture.Transition.Instance.DataList.Count.ShouldBe(2);
        fixture.Transition.Instance.Stage.ShouldBe("review");
    }

    private sealed class ApplicatorFixture
    {
        private ApplicatorFixture(
            TransitionExecutionContext transition,
            ScriptContext scriptContext,
            IInstanceDataReconciliationService reconciler,
            ScriptDataChangeApplicator applicator,
            InstanceDataReconciliationResult success)
        {
            Transition = transition;
            ScriptContext = scriptContext;
            Reconciler = reconciler;
            Applicator = applicator;
            Success = success;
        }

        public TransitionExecutionContext Transition { get; }
        public ScriptContext ScriptContext { get; }
        public IInstanceDataReconciliationService Reconciler { get; }
        public ScriptDataChangeApplicator Applicator { get; }
        public InstanceDataReconciliationResult Success { get; }

        public static ApplicatorFixture Create(
            bool enabled,
            bool withPendingChanges = true,
            bool withScriptInstance = true)
        {
            var live = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "fixture-key");
            live.AddData(Guid.NewGuid(), new JsonData("{\"base\":1}"));
            live.MarkDataPartiallyLoaded();

            var scriptContextBuilder = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
                .SetRuntime(Substitute.For<IRuntimeInfoProvider>());
            if (withScriptInstance)
            {
                scriptContextBuilder = scriptContextBuilder.SetInstance(live.CreateTrackedDataSnapshot());
            }

            var scriptContext = scriptContextBuilder.Build();

            if (withScriptInstance && withPendingChanges)
            {
                scriptContext.Instance!.AddData(Guid.NewGuid(), new JsonData("{\"local\":2}"));
            }

            var transition = new TransitionExecutionContext
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

            // Reconciled outcome: a persisted row strictly newer than the live baseline.
            var donor = Instance.Create(live.Id, "test-flow", "1.0.0", "fixture-key");
            donor.AddData(Guid.NewGuid(), new JsonData("{\"base\":1}"));
            var latest = donor.AddData(Guid.NewGuid(), new JsonData("{\"local\":2}"), VersionStrategy.IncreasePatch);
            var success = new InstanceDataReconciliationResult(latest, [latest], 1, false);

            var reconciler = Substitute.For<IInstanceDataReconciliationService>();
            var options = Options.Create(new WorkflowExecutionOptions
            {
                EnableInstanceDataReconciliation = enabled
            });
            var applicator = new ScriptDataChangeApplicator(reconciler, options);

            return new ApplicatorFixture(transition, scriptContext, reconciler, applicator, success);
        }
    }

    private sealed class NullWorkflowContext : IWorkflowContext
    {
        public Definitions.Workflow? Workflow => null;
        public bool HasWorkflow => false;

        public void SetWorkflow(Definitions.Workflow workflow)
        {
        }
    }
}
