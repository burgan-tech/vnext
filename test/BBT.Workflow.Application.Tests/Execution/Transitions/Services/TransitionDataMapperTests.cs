using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Related;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Services;

public sealed class TransitionDataMapperTests
{
    [Fact]
    public async Task MapTransitionDataAsync_WithMapping_ShouldPreserveRequestMetadataInScriptContext()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        var transition = Transition.Create("map", "state-1", "state-2", TriggerType.Manual, "Patch");
        transition.SetMapping(ScriptCode.FromNative("return context.Body;"));
        var instance = Instance.Create(System.Guid.NewGuid(), workflow.Key, workflow.Version);
        var mapping = new CapturingTransitionMapping();
        var scriptEngine = Substitute.For<IScriptEngine>();
        scriptEngine
            .CompileToInstanceAsync<ITransitionMapping>(Arg.Any<ScriptCode>())
            .ReturnsForAnyArgs(Task.FromResult<ITransitionMapping>(mapping));
        var scriptContextFactory = new ScriptContextFactory(
            Substitute.For<IComponentCacheStore>(),
            NullLogger<ScriptContext>.Instance,
            NullLogger<RelatedInstanceAccessor>.Instance,
            new AmbientRequestRawBodyProvider());
        var sut = new TransitionDataMapper(
            scriptEngine,
            scriptContextFactory,
            Substitute.For<IInstanceRepository>());
        var headers = new Dictionary<string, string?> { ["x-request-id"] = "request-42" };
        var routeValues = new Dictionary<string, string?> { ["orderId"] = "order-7" };

        Result<object?> result;
        using (RawBodyExecutionScope.Set("RAW-ORIGINAL"))
        {
            result = await sut.MapTransitionDataAsync(
                new { amount = 100 },
                transition,
                workflow,
                instance,
                Substitute.For<IRuntimeInfoProvider>(),
                headers,
                routeValues,
                CancellationToken.None);
        }

        result.IsSuccess.ShouldBeTrue();
        var context = mapping.Context.ShouldNotBeNull();
        var capturedHeaders = ((object?)context.Headers).ShouldBeOfType<Dictionary<string, string?>>();
        var capturedRouteValues = ((object?)context.RouteValues).ShouldBeOfType<Dictionary<string, object?>>();
        capturedHeaders["x-request-id"].ShouldBe("request-42");
        capturedRouteValues["orderId"].ShouldBe("order-7");
        capturedHeaders["X-REQUEST-ID"].ShouldBe("request-42");
        capturedRouteValues["ORDERID"].ShouldBe("order-7");
        context.RawBody.ShouldBe("RAW-ORIGINAL");

        await context.DisposeAsync();
    }

    [Fact]
    public async Task MapTransitionDataAsync_HeadersAndCancellationTokenOverload_ShouldRemainSourceCompatible()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        var instance = Instance.Create(System.Guid.NewGuid(), workflow.Key, workflow.Version);
        ITransitionDataMapper sut = new TransitionDataMapper(
            Substitute.For<IScriptEngine>(),
            Substitute.For<IScriptContextFactory>(),
            Substitute.For<IInstanceRepository>());
        var payload = new { amount = 100 };

        var result = await sut.MapTransitionDataAsync(
            payload,
            transition: null,
            workflow,
            instance,
            Substitute.For<IRuntimeInfoProvider>(),
            new Dictionary<string, string?> { ["x-request-id"] = "request-42" },
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(payload);
    }

    [Fact]
    public async Task RouteAwareOverload_WithLegacyImplementation_ShouldFallbackToOriginalContract()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        var instance = Instance.Create(System.Guid.NewGuid(), workflow.Key, workflow.Version);
        var legacyMapper = new LegacyTransitionDataMapper();
        ITransitionDataMapper sut = legacyMapper;
        var payload = new { amount = 100 };

        var result = await sut.MapTransitionDataAsync(
            payload,
            transition: null,
            workflow,
            instance,
            Substitute.For<IRuntimeInfoProvider>(),
            new Dictionary<string, string?> { ["x-request-id"] = "request-42" },
            new Dictionary<string, string?> { ["orderId"] = "order-42" },
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(payload);
        legacyMapper.InvocationCount.ShouldBe(1);
    }

    private sealed class CapturingTransitionMapping : ITransitionMapping
    {
        public ScriptContext? Context { get; private set; }

        public Task<dynamic> Handler(ScriptContext context)
        {
            Context = context;
            return Task.FromResult<dynamic>(new { mapped = true });
        }
    }

    private sealed class LegacyTransitionDataMapper : ITransitionDataMapper
    {
        public int InvocationCount { get; private set; }

        public Task<Result<object?>> MapTransitionDataAsync(
            object? payload,
            Transition? transition,
            Definitions.Workflow workflow,
            Instance instance,
            IRuntimeInfoProvider runtimeInfoProvider,
            IReadOnlyDictionary<string, string?>? headers = null,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(Result<object?>.Ok(payload));
        }
    }
}
