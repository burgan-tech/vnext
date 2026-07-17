using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
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
        services.AddSingleton(Substitute.For<IWorkflowContext>());
        services.AddSingleton(cancellationService);
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
