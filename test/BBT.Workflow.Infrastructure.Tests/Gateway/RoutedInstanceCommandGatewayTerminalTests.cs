using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Gateway;

public sealed class RoutedInstanceCommandGatewayTerminalTests
{
    [Fact]
    public async Task CancelChildAsync_WhenDomainMatches_ExecutesTypedLocalTransition()
    {
        var instanceId = Guid.NewGuid();
        var termination = new TerminationContext(
            TerminationOrigin.ParentCascade,
            Guid.NewGuid(),
            Guid.NewGuid());
        var input = new ChildSubflowCancelInput("1.0.0", termination);
        var commandService = Substitute.For<IInstanceCommandAppService>();
        commandService.TransitionAsync(
                instanceId.ToString(),
                WellKnownTransitionKeys.Cancel,
                Arg.Any<TransitionInput>(),
                CancellationToken.None)
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput()));
        var uow = Substitute.For<IUnitOfWork>();
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(uow);
        var remoteService = Substitute.For<IRemoteInstanceCommandAppService>();
        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.IsDomainMatch("local-domain").Returns(true);
        var services = CreateWorkflowServices("local-domain", "child-flow");
        services.AddSingleton(commandService);
        services.AddSingleton(uowManager);
        services.AddSingleton<LocalInstanceCommandGateway>();
        await using var provider = services.BuildServiceProvider();
        var sut = new RoutedInstanceCommandGateway(
            provider.GetRequiredService<LocalInstanceCommandGateway>(),
            new RemoteInstanceCommandGateway(remoteService),
            runtimeInfo);

        var result = await sut.CancelChildAsync(
            instanceId, "local-domain", "child-flow", input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await commandService.Received(1).TransitionAsync(
            instanceId.ToString(),
            WellKnownTransitionKeys.Cancel,
            Arg.Is<TransitionInput>(value =>
                value.Domain == "local-domain" &&
                value.Workflow == "child-flow" &&
                value.Termination == termination),
            CancellationToken.None);
        await uow.Received(1).CommitAsync(CancellationToken.None);
        await remoteService.DidNotReceive().CancelChildAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<ChildSubflowCancelInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelChildAsync_WhenDomainDiffers_DelegatesTypedInputToRemote()
    {
        var instanceId = Guid.NewGuid();
        var input = new ChildSubflowCancelInput(
            "1.0.0",
            new TerminationContext(TerminationOrigin.ParentCascade, Guid.NewGuid(), Guid.NewGuid()));
        var remoteService = Substitute.For<IRemoteInstanceCommandAppService>();
        remoteService.CancelChildAsync(
                instanceId, "remote-domain", "child-flow", input, CancellationToken.None)
            .Returns(Result.Ok());
        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.IsDomainMatch("remote-domain").Returns(false);
        var sut = new RoutedInstanceCommandGateway(
            new LocalInstanceCommandGateway(Substitute.For<IServiceScopeFactory>()),
            new RemoteInstanceCommandGateway(remoteService),
            runtimeInfo);

        var result = await sut.CancelChildAsync(
            instanceId, "remote-domain", "child-flow", input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await remoteService.Received(1).CancelChildAsync(
            instanceId, "remote-domain", "child-flow", input, CancellationToken.None);
    }

    [Fact]
    public async Task CancelAsync_WhenDomainMatches_DelegatesFullInputToLocalService()
    {
        var input = CreateInput("local-domain");
        var cancellationService = Substitute.For<ISubflowCancellationService>();
        var remoteService = Substitute.For<IRemoteInstanceCommandAppService>();
        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.IsDomainMatch(input.Domain).Returns(true);

        var services = CreateLocalServices(input, cancellationService);
        services.AddSingleton<LocalInstanceCommandGateway>();
        services.AddSingleton(_ => new RemoteInstanceCommandGateway(remoteService));
        services.AddSingleton(provider => new RoutedInstanceCommandGateway(
            provider.GetRequiredService<LocalInstanceCommandGateway>(),
            provider.GetRequiredService<RemoteInstanceCommandGateway>(),
            runtimeInfo));

        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<RoutedInstanceCommandGateway>()
            .CancelAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await cancellationService.Received(1).CancellationAsync(input, CancellationToken.None);
        await remoteService.Received(0).CancelAsync(
            Arg.Any<SubItemCanceledInput>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelAsync_WhenDomainDiffers_DelegatesFullInputToRemoteService()
    {
        var input = CreateInput("remote-domain");
        var remoteService = Substitute.For<IRemoteInstanceCommandAppService>();
        remoteService.CancelAsync(input, CancellationToken.None).Returns(Result.Ok());
        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.IsDomainMatch(input.Domain).Returns(false);
        var sut = new RoutedInstanceCommandGateway(
            new LocalInstanceCommandGateway(Substitute.For<IServiceScopeFactory>()),
            new RemoteInstanceCommandGateway(remoteService),
            runtimeInfo);

        var result = await sut.CancelAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await remoteService.Received(1).CancelAsync(input, CancellationToken.None);
    }

    private static ServiceCollection CreateLocalServices(
        SubItemCanceledInput input,
        ISubflowCancellationService cancellationService)
    {
        var schema = Substitute.For<ICurrentSchema>();
        schema.Name.Returns((string?)null);
        var cacheStore = Substitute.For<IComponentCacheStore>();
        cacheStore.GetFlowAsync(input.Domain, input.Flow, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(DeserializeMinimalWorkflow(input.Flow, input.Domain)));

        var services = new ServiceCollection();
        services.AddSingleton(schema);
        services.AddSingleton(cacheStore);
        services.AddSingleton(cancellationService);
        return services;
    }

    private static ServiceCollection CreateWorkflowServices(string domain, string flow)
    {
        var schema = Substitute.For<ICurrentSchema>();
        schema.Name.Returns((string?)null);
        var cacheStore = Substitute.For<IComponentCacheStore>();
        cacheStore.GetFlowAsync(domain, flow, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(DeserializeMinimalWorkflow(flow, domain)));
        var services = new ServiceCollection();
        services.AddSingleton(schema);
        services.AddSingleton(cacheStore);
        return services;
    }

    private static SubItemCanceledInput CreateInput(string domain) => new()
    {
        InstanceId = Guid.NewGuid(),
        SubInstanceId = Guid.NewGuid(),
        Domain = domain,
        Flow = "parent-flow",
        Version = "1.0.0",
        CanceledState = "canceled-state",
        CanceledAt = DateTime.UtcNow,
        RootInstanceId = Guid.NewGuid(),
        Sync = true,
        Termination = new TerminationContext(
            TerminationOrigin.Direct,
            Guid.NewGuid(),
            Guid.NewGuid())
    };

    private static BBT.Workflow.Definitions.Workflow DeserializeMinimalWorkflow(string key, string domain)
    {
        const string json = """
        {
            "type": "F",
            "timeout": null,
            "labels": [],
            "functions": [],
            "features": [],
            "states": [{"key": "state1", "type": "P", "transitions": []}],
            "sharedTransitions": [],
            "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var workflow = JsonSerializer.Deserialize<BBT.Workflow.Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(key, domain, "sys-flows", "1.0.0"));
        return workflow;
    }
}
