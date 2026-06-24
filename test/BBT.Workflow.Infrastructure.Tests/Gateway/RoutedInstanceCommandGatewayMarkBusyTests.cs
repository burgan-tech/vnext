using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Gateway;

/// <summary>
/// Routing tests for <see cref="RoutedInstanceCommandGateway.MarkBusyAsync"/>.
/// </summary>
public sealed class RoutedInstanceCommandGatewayMarkBusyTests
{
    [Fact]
    public async Task MarkBusyAsync_WhenDomainMismatch_DelegatesToRemoteService()
    {
        var input = new MarkBusyInput { Domain = "remote-domain", Workflow = "wf", InstanceId = Guid.NewGuid() };
        var remoteService = Substitute.For<IRemoteInstanceCommandAppService>();
        remoteService.MarkBusyAsync(Arg.Any<MarkBusyInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.IsDomainMatch(Arg.Any<string>()).Returns(false);

        var unusedScopeFactory = Substitute.For<IServiceScopeFactory>();

        var sut = new RoutedInstanceCommandGateway(
            new LocalInstanceCommandGateway(unusedScopeFactory),
            new RemoteInstanceCommandGateway(remoteService),
            runtimeInfo);

        await sut.MarkBusyAsync(input, CancellationToken.None);

        runtimeInfo.Received(1).IsDomainMatch(input.Domain);
        await remoteService.Received(1).MarkBusyAsync(
            Arg.Is<MarkBusyInput>(
                x => x.Domain == input.Domain
                     && x.Workflow == input.Workflow
                     && x.InstanceId == input.InstanceId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkBusyAsync_WhenDomainMatches_DelegatesLocally_AndNeverCallsRemote()
    {
        const string domain = "local-domain";
        const string workflowKey = "local-wf";

        var input = new MarkBusyInput
        {
            Domain = domain,
            Workflow = workflowKey,
            InstanceId = Guid.NewGuid(),
        };

        var remoteService = Substitute.For<IRemoteInstanceCommandAppService>();
        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.IsDomainMatch(Arg.Any<string>()).Returns(true);

        var schema = Substitute.For<ICurrentSchema>();
        schema.Name.Returns((string?)null);

        var workflow = DeserializeMinimalWorkflow(workflowKey, domain);
        var cacheStore = Substitute.For<IComponentCacheStore>();
        cacheStore.GetFlowAsync(domain, workflowKey, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(workflow));

        var workflowContext = Substitute.For<IWorkflowContext>();

        var busyService = Substitute.For<IInstanceBusyManager>();
        busyService.MarkBusyWithPropagationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(_ => schema);
        services.AddSingleton(_ => cacheStore);
        services.AddSingleton(_ => workflowContext);
        services.AddSingleton(_ => busyService);

        services.AddSingleton<LocalInstanceCommandGateway>();
        services.AddSingleton(_ => new RemoteInstanceCommandGateway(remoteService));
        services.AddSingleton(provider => new RoutedInstanceCommandGateway(
            provider.GetRequiredService<LocalInstanceCommandGateway>(),
            provider.GetRequiredService<RemoteInstanceCommandGateway>(),
            runtimeInfo));

        await using var root = services.BuildServiceProvider();
        var sut = root.GetRequiredService<RoutedInstanceCommandGateway>();

        var result = await sut.MarkBusyAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        runtimeInfo.Received(1).IsDomainMatch(input.Domain);
        await busyService.Received(1).MarkBusyWithPropagationAsync(
            Arg.Is<Guid>(x => x == input.InstanceId),
            Arg.Any<CancellationToken>());
        await remoteService.Received(0).MarkBusyAsync(Arg.Any<MarkBusyInput>(), Arg.Any<CancellationToken>());
    }

    private static BBT.Workflow.Definitions.Workflow DeserializeMinimalWorkflow(string key, string domain)
    {
        const string json = """
        {
            "type": "F",
            "timeout": null,
            "labels": [],
            "functions": [],
            "features": [],
            "states": [
                {
                    "key": "state1",
                    "type": "P",
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        var workflow = JsonSerializer.Deserialize<BBT.Workflow.Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(key, domain, "sys-flows", "1.0.0"));
        return workflow;
    }
}
