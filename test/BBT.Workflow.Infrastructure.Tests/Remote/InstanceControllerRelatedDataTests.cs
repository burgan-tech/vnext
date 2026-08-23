using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Events;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Instances.Related;
using BBT.Workflow.Orchestration.Controllers.Instances;
using BBT.Workflow.Scripting.Related;
using BBT.Workflow.SubFlow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

/// <summary>
/// Covers the internal related-data endpoints added for cross-domain related-instance access
/// (see <see cref="IRelatedInstanceQueryAppService"/>). These endpoints are reached only via Dapr
/// service invocation between runtimes and carry no authorization.
/// </summary>
public sealed class InstanceControllerRelatedDataTests
{
    [Fact]
    public async Task GetRelatedDataAsync_BuildsReferenceFromRouteAndReturnsSnapshot()
    {
        var instanceId = Guid.NewGuid();
        var snapshot = new RelatedInstanceSnapshot
        {
            InstanceId = instanceId,
            Domain = "parent-domain",
            Flow = "parent-flow",
            Status = "A",
            CurrentState = "s1",
            IsCompleted = false
        };
        var relatedService = Substitute.For<IRelatedInstanceQueryAppService>();
        relatedService.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(snapshot));
        var sut = CreateController(relatedService);

        var actionResult = await sut.GetRelatedDataAsync(
            "parent-domain", "parent-flow", instanceId, "1.0.0", CancellationToken.None);

        await relatedService.Received(1).ReadAsync(
            Arg.Is<RelatedInstanceRef>(r =>
                r.InstanceId == instanceId &&
                r.Domain == "parent-domain" &&
                r.Flow == "parent-flow" &&
                r.FlowVersion == "1.0.0"),
            Arg.Any<CancellationToken>());
        var ok = actionResult.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeSameAs(snapshot);
    }

    [Fact]
    public async Task GetRelatedDataAsync_NonexistentInstance_ProducesNoContent()
    {
        // 204 No Content is the DELIBERATE contract for a nonexistent related instance (via
        // AetherControllerBase.FromResult<T> / ResultExtensions.ToActionResult<T> mapping a null
        // Result value), not an accident of the shared mapper. It keeps "absence" (204) distinguishable
        // from "misrouted request" (404) and "fault" (5xx) without a hand-rolled 200-with-null-body,
        // which would break the FromResult house pattern. Task 10's client is written to expect 204
        // here, so this test is load-bearing: a regression to 200/404/anything else must fail loudly.
        var relatedService = Substitute.For<IRelatedInstanceQueryAppService>();
        relatedService.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(null));
        var sut = CreateController(relatedService);

        var actionResult = await sut.GetRelatedDataAsync(
            "parent-domain", "parent-flow", Guid.NewGuid(), null, CancellationToken.None);

        actionResult.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task GetRelatedDataBatchAsync_BuildsOneReferencePerIdAndOmitsUnresolved()
    {
        var foundId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var snapshot = new RelatedInstanceSnapshot
        {
            InstanceId = foundId,
            Domain = "parent-domain",
            Flow = "parent-flow",
            Status = "C",
            IsCompleted = true
        };
        var relatedService = Substitute.For<IRelatedInstanceQueryAppService>();
        relatedService.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok([snapshot]));
        var sut = CreateController(relatedService);
        var input = new RelatedDataBatchInput { InstanceIds = [foundId, missingId] };

        var actionResult = await sut.GetRelatedDataBatchAsync(
            "parent-domain", "parent-flow", input, null, CancellationToken.None);

        await relatedService.Received(1).ReadManyAsync(
            Arg.Is<IReadOnlyList<RelatedInstanceRef>>(refs =>
                refs.Count == 2 &&
                refs[0].InstanceId == foundId && refs[0].Domain == "parent-domain" && refs[0].Flow == "parent-flow" &&
                refs[1].InstanceId == missingId && refs[1].Domain == "parent-domain" && refs[1].Flow == "parent-flow"),
            Arg.Any<CancellationToken>());
        var ok = actionResult.ShouldBeOfType<OkObjectResult>();
        var value = ok.Value.ShouldBeAssignableTo<IReadOnlyList<RelatedInstanceSnapshot>>();
        value!.ShouldHaveSingleItem();
        value[0].InstanceId.ShouldBe(foundId);
    }

    [Fact]
    public async Task GetRelatedDataBatchAsync_TooManyIds_ProducesBadRequestWithoutCallingReadManyAsync()
    {
        // Defence in depth: this endpoint carries no authorization, so it cannot trust the caller's
        // batch size — the real cap (RelatedAccessOptions.MaxResolutionsPerContext) lives in the
        // calling runtime. This is a much higher abuse bound, deliberately above any legitimate batch.
        var relatedService = Substitute.For<IRelatedInstanceQueryAppService>();
        var input = new RelatedDataBatchInput
        {
            InstanceIds = Enumerable.Range(0, RelatedDataBatchInput.MaxInstanceIds + 1)
                .Select(_ => Guid.NewGuid())
                .ToList()
        };
        var sut = CreateController(relatedService);

        var actionResult = await sut.GetRelatedDataBatchAsync(
            "parent-domain", "parent-flow", input, null, CancellationToken.None);

        actionResult.ShouldBeOfType<BadRequestObjectResult>();
        await relatedService.DidNotReceive().ReadManyAsync(
            Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRelatedDataBatchAsync_NoIdsResolve_ProducesOkWithEmptyArray_NotNoContent()
    {
        // Unlike the single read, an empty result LIST is not a null Value: Result<IReadOnlyList<T>>.Ok([])
        // has Value == [] (non-null), so ToActionResult<T> takes the OkObjectResult branch, not the
        // NoContentResult one. This must stay 200 + "[]", because Task 10 deserializes the batch
        // response body into a list — a 204 here would hand it an empty body instead and break
        // deserialization on a code path distinct from the single-read 204 contract.
        var relatedService = Substitute.For<IRelatedInstanceQueryAppService>();
        relatedService.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok([]));
        var sut = CreateController(relatedService);
        var input = new RelatedDataBatchInput { InstanceIds = [Guid.NewGuid()] };

        var actionResult = await sut.GetRelatedDataBatchAsync(
            "parent-domain", "parent-flow", input, null, CancellationToken.None);

        var ok = actionResult.ShouldBeOfType<OkObjectResult>();
        var value = ok.Value.ShouldBeAssignableTo<IReadOnlyList<RelatedInstanceSnapshot>>();
        value!.ShouldBeEmpty();
    }

    private static InstanceController CreateController(
        IRelatedInstanceQueryAppService relatedInstanceQueryAppService)
    {
        var httpContext = new DefaultHttpContext();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        var sut = new InstanceController(
            Substitute.For<IInstanceCommandAppService>(),
            Substitute.For<IInstanceQueryAppService>(),
            Substitute.For<IInstanceRetryAppService>(),
            accessor,
            Substitute.For<ISubflowCompletionService>(),
            Substitute.For<ISubflowStateService>(),
            Substitute.For<ISubflowFaultService>(),
            Substitute.For<ISubflowCancellationService>(),
            Substitute.For<IInstanceCancellationService>(),
            Substitute.For<IChildSubflowCancellationService>(),
            Substitute.For<IChildSubflowFaultService>(),
                Substitute.For<IInstanceCommandGateway>(),
            Substitute.For<IEventAppService>(),
            relatedInstanceQueryAppService,
            Substitute.For<ICurrentUser>())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        return sut;
    }
}
