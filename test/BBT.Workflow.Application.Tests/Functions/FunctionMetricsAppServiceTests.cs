using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions;
using BBT.Workflow.Metrics;
using BBT.Workflow.Runtime;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Functions;

/// <summary>
/// Unit tests for <see cref="FunctionMetricsAppService"/> (vnext-client-sdk-core#60, item D): the
/// filter/paging it hands the repository, the item/summary mapping, and the domain- vs flow-scoped
/// route selection.
/// </summary>
public class FunctionMetricsAppServiceTests
{
    private const string Domain = "sample";
    private const string FunctionKey = "get-report";

    private readonly IFunctionExecutionRepository _repository = Substitute.For<IFunctionExecutionRepository>();
    private readonly IUrlTemplateBuilder _urlTemplateBuilder = Substitute.For<IUrlTemplateBuilder>();
    private readonly FunctionMetricsAppService _service;

    public FunctionMetricsAppServiceTests()
    {
        _urlTemplateBuilder.BuildDomainFunctionUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}/functions/{ci.ArgAt<string>(1)}");
        _urlTemplateBuilder.BuildFunctionListUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(ci => $"{ci.ArgAt<string>(0)}/workflows/{ci.ArgAt<string>(1)}/functions/{ci.ArgAt<string>(2)}");

        _repository.QueryAsync(Arg.Any<FunctionExecutionQuery>(), Arg.Any<CancellationToken>())
            .Returns(new FunctionExecutionQueryResult([], false));
        _repository.SummarizeAsync(Arg.Any<FunctionExecutionQuery>(), Arg.Any<CancellationToken>())
            .Returns(new FunctionExecutionSummary(0, null, null, 0));

        _service = new FunctionMetricsAppService(
            Substitute.For<IRuntimeInfoProvider>(),
            _repository,
            _urlTemplateBuilder,
            Substitute.For<BBT.Aether.Application.Pagination.IPaginationLinkGenerator>());
    }

    [Fact]
    public async Task GetFunctionMetricsAsync_MapsItemsAndSummary()
    {
        var execId = Guid.NewGuid();
        _repository.QueryAsync(Arg.Any<FunctionExecutionQuery>(), Arg.Any<CancellationToken>())
            .Returns(new FunctionExecutionQueryResult(
            [
                FunctionExecution.Record(execId, Domain, FunctionKey, "1.0.0", TaskScope.Domain,
                    null, null, DateTime.UtcNow, 123.4, succeeded: false, statusCode: null,
                    errorCode: "Task:Http:503", fromCache: false, traceId: "trace-abc")
            ], HasNext: true));
        _repository.SummarizeAsync(Arg.Any<FunctionExecutionQuery>(), Arg.Any<CancellationToken>())
            .Returns(new FunctionExecutionSummary(7, 100.0, 480.0, 0.25));

        var result = await _service.GetFunctionMetricsAsync(Input(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var output = result.Value!;
        var item = output.Items.ShouldHaveSingleItem();
        item.ExecutionId.ShouldBe(execId);
        item.Scope.ShouldBe("D");
        item.Succeeded.ShouldBeFalse();
        item.Status.ShouldBe("faulted");        // derived string mirroring the task-metrics vocabulary
        item.Error.ShouldBe("Task:Http:503");
        item.DurationMs.ShouldBe(123.4);
        item.TraceId.ShouldBe("trace-abc");     // links the row to its APM/ELK trace

        output.Summary.ShouldNotBeNull();
        output.Summary!.Count.ShouldBe(7);
        output.Summary.P50Ms.ShouldBe(100.0);
        output.Summary.P95Ms.ShouldBe(480.0);
        output.Summary.FailureRate.ShouldBe(0.25);
    }

    [Fact]
    public async Task GetFunctionMetricsAsync_DomainScope_UsesDomainRouteAndNoWorkflowFilter()
    {
        FunctionExecutionQuery? captured = null;
        _repository.QueryAsync(Arg.Do<FunctionExecutionQuery>(q => captured = q), Arg.Any<CancellationToken>())
            .Returns(new FunctionExecutionQueryResult([], false));

        await _service.GetFunctionMetricsAsync(Input(workflow: null), CancellationToken.None);

        captured!.Workflow.ShouldBeNull();
        _urlTemplateBuilder.Received(1).BuildDomainFunctionUrl(Domain, FunctionKey, Arg.Any<string?>());
        _urlTemplateBuilder.DidNotReceive().BuildFunctionListUrl(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetFunctionMetricsAsync_FlowScope_UsesFlowRouteAndPassesWorkflowAndFilters()
    {
        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow;
        FunctionExecutionQuery? captured = null;
        _repository.QueryAsync(Arg.Do<FunctionExecutionQuery>(q => captured = q), Arg.Any<CancellationToken>())
            .Returns(new FunctionExecutionQueryResult([], false));

        await _service.GetFunctionMetricsAsync(
            Input(workflow: "north-star", from: from, to: to, succeeded: false), CancellationToken.None);

        captured!.Workflow.ShouldBe("north-star");
        captured.From.ShouldBe(from);
        captured.To.ShouldBe(to);
        captured.Succeeded.ShouldBe(false);
        _urlTemplateBuilder.Received(1).BuildFunctionListUrl(Domain, "north-star", FunctionKey, Arg.Any<string?>());
    }

    [Fact]
    public async Task GetFunctionMetricsAsync_ClampsPageSizeToMax()
    {
        FunctionExecutionQuery? captured = null;
        _repository.QueryAsync(Arg.Do<FunctionExecutionQuery>(q => captured = q), Arg.Any<CancellationToken>())
            .Returns(new FunctionExecutionQueryResult([], false));

        await _service.GetFunctionMetricsAsync(Input(pageSize: 10_000), CancellationToken.None);

        captured!.PageSize.ShouldBe(GetFunctionMetricsInput.MaxPageSize);
    }

    private static GetFunctionMetricsInput Input(
        string? workflow = null, int pageSize = 20, DateTime? from = null, DateTime? to = null, bool? succeeded = null) => new()
    {
        Domain = Domain,
        Workflow = workflow,
        FunctionKey = FunctionKey,
        Page = 1,
        PageSize = pageSize,
        From = from,
        To = to,
        Succeeded = succeeded
    };
}
