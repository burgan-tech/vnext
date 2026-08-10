using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Related;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Evaluation;

public sealed class AutoConditionEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_ShouldExposeRouteValuesCaseInsensitivelyToConditionScript()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        var transition = Transition.Create(
            "automatic",
            "state-1",
            "state-2",
            TriggerType.Automatic,
            "Patch");
        transition.SetRule("inline", "return true;");
        var instance = Instance.Create(Guid.NewGuid(), workflow.Key, workflow.Version);
        var current = State.Create("state-1", StateType.Intermediate, StateSubType.None, "Patch");
        var conditionService = Substitute.For<ITaskConditionService>();
        ScriptContext? capturedContext = null;
        conditionService
            .ExecuteConditionAsync(
                Arg.Any<ScriptCode>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedContext = callInfo.ArgAt<ScriptContext>(1);
                return Task.FromResult(Result<bool>.Ok(true));
            });
        var scriptContextFactory = new ScriptContextFactory(
            Substitute.For<IComponentCacheStore>(),
            NullLogger<ScriptContext>.Instance,
            NullLogger<RelatedInstanceAccessor>.Instance);
        var sut = new AutoConditionEvaluator(
            conditionService,
            scriptContextFactory,
            Substitute.For<IInstanceRepository>(),
            NullLogger<AutoConditionEvaluator>.Instance,
            Substitute.For<IRuntimeInfoProvider>());
        var context = new TransitionExecutionContext
        {
            Domain = workflow.Domain,
            InstanceId = instance.Id,
            WorkflowKey = workflow.Key,
            TransitionKey = transition.Key,
            Trigger = transition.TriggerType,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = current,
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16],
            Headers = new Dictionary<string, string?> { ["X-Request-Id"] = "request-42" },
            RouteValues = new Dictionary<string, string?> { ["OrderId"] = "order-42" }
        };

        var result = await sut.EvaluateAsync(transition, context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var scriptContext = capturedContext.ShouldNotBeNull();
        var headers = ((object?)scriptContext.Headers)
            .ShouldBeOfType<Dictionary<string, string?>>();
        var routeValues = ((object?)scriptContext.RouteValues)
            .ShouldBeOfType<Dictionary<string, object?>>();
        headers["x-request-id"].ShouldBe("request-42");
        routeValues["ORDERID"].ShouldBe("order-42");

        await scriptContext.DisposeAsync();
    }
}
