using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.Runtime;
using BBT.Workflow.RuntimeCaller;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// The host wiring the sub-endpoint gate depends on (vnext#1265), which no in-process pipeline test can
/// see: the strip must run before anything reads or logs request headers in the real Orchestration
/// pipeline, every host that signs must register the identity, and Orchestration's own body-logging
/// redaction must name the header.
/// </summary>
public sealed class SubEndpointGateWiringTests
{
    [Fact]
    public void OrchestrationPipeline_ShouldStripTheToken_BeforeAnythingReadsOrLogsHeaders()
    {
        var source = ReadSource("orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Microsoft/AspNetCore/Builder/OrchestrationApiApplicationBuilderExtensions.cs");

        var strip = source.IndexOf("app.UseRuntimeCallerTokenStrip();", StringComparison.Ordinal);
        strip.ShouldBeGreaterThan(0, "the Orchestration pipeline no longer strips the runtime-caller token");
        foreach (var later in new[] { "app.UseCorrelationId();", "app.UseCurrentUser();", "app.UseHttpBodyLogging();", "app.MapControllers();" })
        {
            var at = source.IndexOf(later, StringComparison.Ordinal);
            at.ShouldBeGreaterThan(strip, $"{later} must come after the strip");
        }
    }

    [Theory]
    [InlineData("orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Microsoft/Extensions/DependencyInjection/OrchestrationApiServiceCollectionExtensions.cs", "AddRuntimeCallerIdentity(configuration, runsFlowScripts: true)")]
    [InlineData("orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Microsoft/Extensions/DependencyInjection/OrchestrationApiServiceCollectionExtensions.cs", "AddSubEndpointGate()")]
    [InlineData("execution/BBT.Workflow.Execution.HttpApi.Host/Microsoft/Extensions/DependencyInjection/ExecutionApiServiceCollectionExtensions.cs", "AddRuntimeCallerIdentity(configuration, runsFlowScripts: false)")]
    [InlineData("workers/BBT.Workflow.Workers.Inbox/Microsoft/Extensions/DependencyInjection/InboxWorkerServiceCollectionExtensions.cs", "AddRuntimeCallerIdentity(configuration, runsFlowScripts: false)")]
    public void EverySigningHost_ShouldRegisterTheIdentity(string file, string registration)
        => ReadSource(file).ShouldContain(registration);

    [Fact]
    public void InboxCompletionBackup_ShouldForwardWithTheSigningTarget()
    {
        // The Inbox has no test project and its backup carries no user: without the target argument the
        // forward goes out unsigned, every unit test stays green, and a gated `complete` drops it.
        var source = ReadSource("workers/BBT.Workflow.Workers.Inbox/Handlers/Instances/InstanceSubCompletedEventHandler.cs");

        source.ShouldContain("var (route, target) = GatedOrchestrationRoutes.Complete(");
        source.ShouldContain("cancellationToken, target);");
    }

    [Fact]
    public void OrchestrationBodyLogging_ShouldRedactTheTokenHeader()
    {
        using var settings = JsonDocument.Parse(ReadSource("orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json"));
        var names = settings.RootElement.GetProperty("Telemetry").GetProperty("Logging").GetProperty("Body")
            .GetProperty("AdditionalSensitiveHeaderNames");

        names.EnumerateArray().ShouldContain(name => name.GetString() == RuntimeCallerToken.HeaderName);
    }

    [Fact]
    public void AddRuntimeCallerIdentity_ShouldResolveOneIdentity_AsSignerAndVerifier()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(Substitute.For<IRuntimeInfoProvider>());
        services.AddRuntimeCallerIdentity(configuration, runsFlowScripts: false);
        using var provider = services.BuildServiceProvider();

        var signer = provider.GetRequiredService<IRuntimeCallerSigner>();
        signer.ShouldBeOfType<RuntimeCallerIdentity>();
        provider.GetRequiredService<IRuntimeCallerVerifier>().ShouldBeSameAs(signer);
        ((IRuntimeCallerVerifier)signer).IsConfigured.ShouldBeFalse();
    }

    private static string ReadSource(string relativePath)
    {
        // `.git` is a directory in a clone and a file in a worktree; either marks the root.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
               && !File.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("could not locate the repository root");
        var path = Path.Combine(directory!.FullName, relativePath);
        File.Exists(path).ShouldBeTrue($"source not found at {path}");
        return File.ReadAllText(path);
    }
}
