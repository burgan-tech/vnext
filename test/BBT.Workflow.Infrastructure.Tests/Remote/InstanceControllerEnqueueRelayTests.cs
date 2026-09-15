using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Events;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Instances.Related;
using BBT.Workflow.Orchestration.Controllers.Instances;
using BBT.Workflow.Shared;
using BBT.Workflow.SubFlow;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

/// <summary>
/// Pins the <c>transitions/{key}/enqueue</c> relay — the endpoint the Inbox forwards
/// <see cref="TransitionContinuationRequested"/> events to when outbox continuations are active
/// (<c>WorkflowExecution:DirectEnqueueContinuations=false</c>, or the direct-enqueue fallback).
/// This path had zero traffic in the 2026-08-30 acceptance run (issue #935), and the mapping is a
/// hand-written field copy: a field added to both carriers but forgotten here ships silently — that
/// is exactly how the relay dropped <c>SubflowChainReserved</c>, making a leaf reject its own
/// forward as Busy. The reflection guard below fails for the NEXT such field.
/// </summary>
public sealed class InstanceControllerEnqueueRelayTests
{
    [Fact]
    public async Task EnqueueTransitionAsync_RelaysTheCarrierOntoTheJobPayload()
    {
        var (enqueuer, sut) = CreateController();
        TransitionJobPayload? payload = null;
        Guid capturedJobId = default;
        await enqueuer.EnqueueAsync(
            Arg.Do<TransitionJobPayload>(p => payload = p),
            Arg.Do<Guid>(id => capturedJobId = id),
            Arg.Any<CancellationToken>());
        var continuation = CreateFullyPopulatedContinuation();

        await sut.EnqueueTransitionAsync(
            continuation.Domain, continuation.Flow, continuation.InstanceId, continuation.TransitionKey,
            continuation, CancellationToken.None);

        payload.ShouldNotBeNull();
        // The job id is threaded as the enqueue argument so BackgroundJobInfo.Id == InstanceJob.JobId.
        capturedJobId.ShouldBe(continuation.JobId);
        payload!.Workflow.ShouldBe(continuation.Flow);
        payload.ExecutionActor.ShouldBe(ExecutionActor.User);
        payload.CallerSync.ShouldBeFalse();
        // The activation episode must survive the relay, or the settling hop reports a partial span.
        payload.EpisodeStartedAt.ShouldBe(continuation.EpisodeStartedAt);
        payload.EpisodeTrigger.ShouldBe(continuation.EpisodeTrigger);
        payload.EpisodeTransitionKey.ShouldBe(continuation.EpisodeTransitionKey);
        payload.EpisodeTraceRoot.ShouldBe(continuation.EpisodeTraceRoot);
        // The accept-time chain-reserve claim must survive the relay, or the leaf rejects its own
        // forward as Busy (Instance:100031) — the regression this class exists for.
        payload.SubflowChainReserved.ShouldBeTrue();
    }

    /// <summary>
    /// Every event property with a same-named (or explicitly renamed) settable counterpart on
    /// <see cref="TransitionJobPayload"/> must arrive with the event's value. A field added to both
    /// carriers but not to the controller's copy fails here instead of shipping a silent drop.
    /// </summary>
    [Fact]
    public async Task EnqueueTransitionAsync_CopiesEverySharedCarrierField()
    {
        var (enqueuer, sut) = CreateController();
        TransitionJobPayload? payload = null;
        await enqueuer.EnqueueAsync(
            Arg.Do<TransitionJobPayload>(p => payload = p), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        var continuation = CreateFullyPopulatedContinuation();

        await sut.EnqueueTransitionAsync(
            continuation.Domain, continuation.Flow, continuation.InstanceId, continuation.TransitionKey,
            continuation, CancellationToken.None);

        payload.ShouldNotBeNull();
        // Renames, and members that deliberately do NOT land on the payload:
        //   JobId          → the separate enqueue argument (asserted in the test above)
        //   RootInstanceId → Activity baggage on the Inbox hop (X-Root-Instance-Id), never payload
        //   ExecutionActor → string on the wire, enum on the payload (asserted above + fallback below)
        var renames = new Dictionary<string, string> { ["Flow"] = nameof(TransitionJobPayload.Workflow) };
        var notRelayed = new[]
        {
            nameof(TransitionContinuationRequested.JobId),
            nameof(TransitionContinuationRequested.RootInstanceId),
            nameof(TransitionContinuationRequested.ExecutionActor)
        };

        var payloadProps = typeof(TransitionJobPayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name);
        foreach (var eventProp in typeof(TransitionContinuationRequested)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => !notRelayed.Contains(p.Name)))
        {
            var targetName = renames.GetValueOrDefault(eventProp.Name, eventProp.Name);
            if (!payloadProps.TryGetValue(targetName, out var payloadProp))
                continue; // event-only member (nothing on the payload to copy to)

            var expected = eventProp.GetValue(continuation);
            var actual = payloadProp.GetValue(payload);
            Normalize(actual).ShouldBe(Normalize(expected),
                $"'{eventProp.Name}' was not relayed onto TransitionJobPayload.{targetName}");
        }
    }

    [Fact]
    public async Task EnqueueTransitionAsync_UnparsableActor_FallsBackToSystem()
    {
        var (enqueuer, sut) = CreateController();
        TransitionJobPayload? payload = null;
        await enqueuer.EnqueueAsync(
            Arg.Do<TransitionJobPayload>(p => payload = p), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        var continuation = CreateFullyPopulatedContinuation(actor: "not-an-actor");

        await sut.EnqueueTransitionAsync(
            continuation.Domain, continuation.Flow, continuation.InstanceId, continuation.TransitionKey,
            continuation, CancellationToken.None);

        payload.ShouldNotBeNull();
        payload!.ExecutionActor.ShouldBe(ExecutionActor.System);
    }

    /// <summary>JsonElement has no value equality; compare payload JSON by raw text.</summary>
    private static object? Normalize(object? value) =>
        value is JsonElement element ? element.GetRawText() : value;

    private static TransitionContinuationRequested CreateFullyPopulatedContinuation(string actor = "user")
    {
        using var data = JsonDocument.Parse("""{ "amount": 42 }""");
        return new TransitionContinuationRequested
        {
            InstanceId = Guid.NewGuid(),
            Domain = "test-domain",
            Flow = "test-flow",
            Version = "1.2.3",
            TransitionKey = "approve",
            JobName = "flow.transition:approve:0001",
            JobId = Guid.NewGuid(),
            Data = data.RootElement.Clone(),
            InstanceKey = "instance-key",
            Tags = ["a", "b"],
            Stage = "stage-1",
            Headers = new Dictionary<string, string?> { ["x-request-id"] = "req-1" },
            RouteValues = new Dictionary<string, string?> { ["domain"] = "test-domain" },
            ExecutionActor = actor,
            TraceParent = "00-11111111111111111111111111111111-2222222222222222-01",
            TraceState = "vendor=1",
            RequestId = "req-1",
            CorrelationId = "corr-1",
            ChainDepth = 3,
            SubflowChainReserved = true,
            RootInstanceId = Guid.NewGuid(),
            TraceRoot = "00-11111111111111111111111111111111-3333333333333333-01",
            ParentTraceRoot = "00-11111111111111111111111111111111-4444444444444444-01",
            LaneSeq = 7,
            EpisodeStartedAt = new DateTimeOffset(2026, 9, 15, 8, 30, 0, TimeSpan.Zero),
            EpisodeTrigger = "http",
            EpisodeTransitionKey = "start",
            EpisodeTraceRoot = "00-11111111111111111111111111111111-5555555555555555-01"
        };
    }

    private static (ITransitionJobEnqueuer enqueuer, InstanceController sut) CreateController()
    {
        var enqueuer = Substitute.For<ITransitionJobEnqueuer>();
        var controller = new InstanceController(
            Substitute.For<IInstanceCommandAppService>(),
            Substitute.For<IInstanceQueryAppService>(),
            Substitute.For<IInstanceRetryAppService>(),
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<ISubflowCompletionService>(),
            Substitute.For<ISubflowStateService>(),
            Substitute.For<ISubflowFaultService>(),
            Substitute.For<ISubflowCancellationService>(),
            Substitute.For<IInstanceCancellationService>(),
            Substitute.For<IChildSubflowCancellationService>(),
            Substitute.For<IChildSubflowFaultService>(),
            enqueuer,
            Substitute.For<IInstanceCommandGateway>(),
            Substitute.For<IEventAppService>(),
            Substitute.For<IRelatedInstanceQueryAppService>(),
            new DefaultCallerRoleResolver(Substitute.For<ICurrentUser>()));
        return (enqueuer, controller);
    }
}
