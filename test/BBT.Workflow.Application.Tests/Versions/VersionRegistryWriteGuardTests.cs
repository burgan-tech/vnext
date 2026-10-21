using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Policies;
using BBT.Workflow.Definitions.Specifications;
using BBT.Workflow.Logging;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Validation;
using BBT.Workflow.Versions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Versions;

public class VersionRegistryWriteGuardTests
{
    private const string Domain = "core";
    private const string Flow = "customer-onboarding";

    private readonly ICacheBackend<Definitions.Workflow> _backend = Substitute.For<ICacheBackend<Definitions.Workflow>>();
    private readonly VersionRegistryWriteGuard _guard;

    public VersionRegistryWriteGuardTests()
    {
        Publish("2.0.0", "2.1.0", "2.1.1", "2.3.0");
        _guard = new VersionRegistryWriteGuard(_backend, Substitute.For<ILogger<VersionRegistryWriteGuard>>());
    }

    [Theory]
    [InlineData("""{"type":"Flow","defaultVersion":""}""")]
    [InlineData("""{"type":"Flow","defaultVersion":"   "}""")]
    [InlineData("""{"type":"Flow","defaultVersion":2}""")]
    public async Task Validate_ShouldReject_BlankOrNonString(string json)
    {
        var result = await _guard.ValidateAsync(Domain, Flow, Parse(json), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FlowDefaultVersionInvalid);
    }

    [Theory]
    [InlineData("""{"type":"Flow","defaultVersion":"latest"}""")]
    [InlineData("""{"type":"Flow","defaultVersion":"LATEST"}""")]
    public async Task Validate_ShouldAccept_LatestPointer(string json)
    {
        var result = await _guard.ValidateAsync(Domain, Flow, Parse(json), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _backend.DidNotReceiveWithAnyArgs().LoadAllByKeyAsync(default!, default!, default);
    }

    [Fact]
    public async Task Validate_ShouldReject_WhenPointerMatchesNoPublishedVersion()
    {
        var result = await _guard.ValidateAsync(
            Domain, Flow, Parse("""{"type":"Flow","defaultVersion":"2.4"}"""), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FlowDefaultVersionInvalid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Validate_ShouldReject_WhenFlowNotPublished(bool backendFails)
    {
        _backend.LoadAllByKeyAsync(Domain, "not-a-flow", Arg.Any<CancellationToken>())
            .Returns(backendFails
                ? Result<List<Definitions.Workflow>>.Fail(
                    BBT.Aether.Results.Error.NotFound(WorkflowErrorCodes.CacheItemNotFound, "missing"))
                : Result<List<Definitions.Workflow>>.Ok([]));

        var result = await _guard.ValidateAsync(
            Domain, "not-a-flow", Parse("""{"type":"Flow","defaultVersion":"2"}"""), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FlowDefaultVersionInvalid);
    }

    [Fact]
    public async Task Validate_ShouldAccept_NullPointer()
    {
        var result = await _guard.ValidateAsync(
            Domain, Flow, Parse("""{"type":"Flow","defaultVersion":null,"reason":"reset"}"""), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _backend.DidNotReceiveWithAnyArgs().LoadAllByKeyAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("2.1")]
    [InlineData("2.1.1")]
    public async Task Validate_ShouldAccept_PartialPointerWithMatch(string pointer)
    {
        var result = await _guard.ValidateAsync(
            Domain, Flow, Parse($$"""{"type":"Flow","defaultVersion":"{{pointer}}"}"""), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("2.1.0-pkg.1.0.168")]
    [InlineData("2.1.0-pkg.1.0.168+core")]
    public async Task Validate_ShouldAccept_FullPackagePointer(string pointer)
    {
        Publish("2.1.0-pkg.1.0.168+core", "2.2.0-pkg.1.0.170+core");

        var result = await _guard.ValidateAsync(
            Domain, Flow, Parse($$"""{"type":"Flow","defaultVersion":"{{pointer}}"}"""), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("2.1.0-pkg.1.0")]
    [InlineData("2.1.0-pkg.1")]
    public async Task Validate_ShouldReject_PartialPackagePointer(string pointer)
    {
        Publish("2.1.0-pkg.1.0.168+core", "2.2.0-pkg.1.0.170+core");

        var result = await _guard.ValidateAsync(
            Domain, Flow, Parse($$"""{"type":"Flow","defaultVersion":"{{pointer}}"}"""), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FlowDefaultVersionInvalid);
    }

    [Fact]
    public async Task Validate_ShouldAccept_WhenThereIsNoPayload()
    {
        (await _guard.ValidateAsync(Domain, Flow, null, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        (await _guard.ValidateAsync(Domain, Flow, Parse("{}"), CancellationToken.None)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Validate_ShouldPropagate_BackendReadFailure()
    {
        _backend.LoadAllByKeyAsync(Domain, "boom", Arg.Any<CancellationToken>())
            .Returns(Result<List<Definitions.Workflow>>.Fail(
                BBT.Aether.Results.Error.Transient("Cache:999", "db down")));

        var result = await _guard.ValidateAsync(
            Domain, "boom", Parse("""{"defaultVersion":"2"}"""), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Cache:999");
    }

    [Fact]
    public async Task GuardStep_ShouldFailBeforeBusy_AndLeaveInstanceActive()
    {
        // The guard runs inside ValidateSchemaAsync: the accept/validation phase, which precedes
        // admission (the Busy reserve) and the pipeline, so a rejection can neither flip the
        // instance Busy, fault it, nor create a transition record.
        var guard = Substitute.For<IVersionRegistryWriteGuard>();
        guard.ValidateAsync(Domain, Flow, Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(WorkflowErrors.FlowDefaultVersionInvalid(Domain, Flow, "2.4")));
        var service = new TransitionValidationService(
            new TransitionExecutionPolicy(
                new CompositeTransitionSpecification(
                    [], Substitute.For<ILogger<CompositeTransitionSpecification>>())),
            Substitute.For<IJsonSchemaValidator>(),
            Substitute.For<IComponentCacheStore>(),
            guard);
        var context = CreateContext(RuntimeSysSchemaInfo.VersionRegistry, Flow, """{"defaultVersion":"2.4"}""");

        var result = await service.ValidateAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FlowDefaultVersionInvalid);
        context.Instance.Status.ShouldBe(InstanceStatus.Active);
        context.Instance.IsBusy.ShouldBeFalse();
        context.Directives.PostCommitJobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidationService_ShouldNotCallGuard_ForOtherFlows()
    {
        var guard = Substitute.For<IVersionRegistryWriteGuard>();
        var service = new TransitionValidationService(
            new TransitionExecutionPolicy(
                new CompositeTransitionSpecification(
                    [], Substitute.For<ILogger<CompositeTransitionSpecification>>())),
            Substitute.For<IJsonSchemaValidator>(),
            Substitute.For<IComponentCacheStore>(),
            guard);
        var context = CreateContext("customer-onboarding", "abc", """{"defaultVersion":"zzz"}""");

        var result = await service.ValidateSchemaAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await guard.DidNotReceiveWithAnyArgs().ValidateAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ValidationService_ShouldPassPayloadAndInstanceKey_ToGuard_ForRegistry()
    {
        var guard = Substitute.For<IVersionRegistryWriteGuard>();
        guard.ValidateAsync(default!, default!, default, default).ReturnsForAnyArgs(Result.Ok());
        var service = new TransitionValidationService(
            new TransitionExecutionPolicy(
                new CompositeTransitionSpecification(
                    [], Substitute.For<ILogger<CompositeTransitionSpecification>>())),
            Substitute.For<IJsonSchemaValidator>(),
            Substitute.For<IComponentCacheStore>(),
            guard);
        var context = CreateContext(RuntimeSysSchemaInfo.VersionRegistry, Flow, """{"defaultVersion":"2.1"}""");

        var result = await service.ValidateSchemaAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await guard.Received(1).ValidateAsync(
            Domain, Flow,
            Arg.Is<JsonElement?>(e => e!.Value.GetProperty("defaultVersion").GetString() == "2.1"),
            Arg.Any<CancellationToken>());
    }

    private void Publish(params string[] versions)
    {
        var list = versions.Select(v =>
        {
            var wf = Definitions.Workflow.Create();
            wf.SetReference(new Reference(Flow, Domain, "sys-flows", v));
            return wf;
        }).ToList();
        _backend.LoadAllByKeyAsync(Domain, Flow, Arg.Any<CancellationToken>())
            .Returns(Result<List<Definitions.Workflow>>.Ok(list));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static TransitionExecutionContext CreateContext(string workflowKey, string instanceKey, string json)
    {
        var instance = Instance.Create(Guid.NewGuid(), workflowKey, "1.0.0", instanceKey);
        var transition = Transition.Create("set-default", "active", "active", TriggerType.Manual, "Patch");
        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = Domain,
            WorkflowKey = workflowKey,
            TransitionKey = transition.Key,
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = Definitions.Workflow.Create(),
            Current = StateFactory.CreateDefault("active"),
            Transition = transition,
            Instance = instance,
            Data = Parse(json),
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }
}
