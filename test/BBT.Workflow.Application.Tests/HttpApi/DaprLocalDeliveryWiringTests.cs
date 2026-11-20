using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BBT.Workflow.Orchestration.Controllers.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// Pins how the Dapr local-delivery guard is wired into the orchestration host: which actions carry
/// the metadata, that the real Dapr jobs route is recognised, and how the token option resolves.
/// </summary>
[Collection(nameof(DaprLocalDeliveryWiringCollection))]
public sealed class DaprLocalDeliveryWiringTests
{
    private const string EnvName = "APP_API_TOKEN";

    [Theory]
    [InlineData(nameof(InstanceController.HandleEventAsync))]
    [InlineData(nameof(InstanceController.ScheduleStartAsync))]
    public void DaprTriggeredActions_CarryLocalDeliveryMetadata(string action)
        => typeof(InstanceController).GetMethod(action)!
            .GetCustomAttributes(typeof(DaprLocalDeliveryOnlyAttribute), false).ShouldNotBeEmpty();

    [Fact]
    public void OnlyDaprTriggeredActions_CarryLocalDeliveryMetadata()
    {
        var carriers = typeof(InstanceController)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(typeof(DaprLocalDeliveryOnlyAttribute), false).Length > 0)
            .Select(m => m.Name)
            .OrderBy(n => n)
            .ToList();

        carriers.ShouldBe([nameof(InstanceController.HandleEventAsync), nameof(InstanceController.ScheduleStartAsync)]);
        typeof(InstanceController).GetCustomAttributes(typeof(DaprLocalDeliveryOnlyAttribute), true).ShouldBeEmpty();
    }

    [Fact]
    public async Task RealDaprJobsRoute_IsTreatedAsGuarded()
    {
        // The very call the host makes (Aether's UseDaprScheduledJobHandler -> Dapr.Jobs MapDaprScheduledJobHandler).
        var app = WebApplication.CreateBuilder().Build();
        app.UseDaprScheduledJobHandler();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        var job = endpoints.SingleOrDefault(e => e.RoutePattern.RawText?.Contains("job", StringComparison.OrdinalIgnoreCase) == true);
        job.ShouldNotBeNull($"routes: {string.Join(", ", endpoints.Select(e => e.RoutePattern.RawText))}");

        var nextCalled = false;
        var middleware = new DaprLocalDeliveryMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            new FixedOptionsMonitor(new DaprLocalDeliveryOptions { Mode = DaprLocalDeliveryMode.Enforce }),
            new DaprAppApiToken("s3cret"),
            NullLogger<DaprLocalDeliveryMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.SetEndpoint(job);
        await middleware.InvokeAsync(context); // no token: a guarded endpoint must be rejected

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        nextCalled.ShouldBeFalse();
        job.RoutePattern.RawText.ShouldBe("/job/{jobName}");
    }

    [Fact]
    public void Options_BindModeFromConfiguration()
    {
        var provider = Build(new Dictionary<string, string?> { ["Dapr:LocalDelivery:Mode"] = "Enforce" });

        provider.GetRequiredService<IOptions<DaprLocalDeliveryOptions>>().Value.Mode.ShouldBe(DaprLocalDeliveryMode.Enforce);
    }

    [Fact]
    public void Options_WithoutConfig_DefaultToLogOnly()
        => Build(new Dictionary<string, string?>()).GetRequiredService<IOptions<DaprLocalDeliveryOptions>>().Value.Mode
            .ShouldBe(DaprLocalDeliveryMode.LogOnly);

    [Fact]
    public void EnvironmentVariable_IsIgnored()
    {
        var previous = Environment.GetEnvironmentVariable(EnvName);
        try
        {
            Environment.SetEnvironmentVariable(EnvName, "from-env");

            var provider = Build(new Dictionary<string, string?>());

            provider.GetRequiredService<DaprAppApiToken>().IsConfigured.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, previous);
        }
    }

    [Fact]
    public void TokenFile_IsResolvedFromRuntimeCallerSection()
    {
        var file = WriteTokenFile("tok");
        try
        {
            var provider = Build(TokenFileConfig(file));

            provider.GetRequiredService<DaprAppApiToken>().Value.ShouldBe("tok");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("tok", false)]
    [InlineData("wrong", true)]
    public async Task Pipeline_AcceptsTokenFromFile_AndRejectsAnythingElse(string presented, bool rejected)
    {
        var file = WriteTokenFile("tok");
        try
        {
            var config = TokenFileConfig(file);
            config["Dapr:LocalDelivery:Mode"] = "Enforce";

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
            builder.Services.AddSingleton<IConfiguration>(configuration);
            builder.Services.AddSingleton(Substitute.For<IRuntimeInfoProvider>());
            builder.Services.AddRuntimeCallerIdentity(configuration, runsFlowScripts: true);
            builder.Services.AddDaprLocalDelivery(configuration);
            await using var app = builder.Build();
            app.UseMiddleware<DaprLocalDeliveryMiddleware>();
            app.MapPost("/guarded", () => Results.Ok()).WithMetadata(new DaprLocalDeliveryOnlyAttribute());
            await app.StartAsync();

            using var request = new HttpRequestMessage(HttpMethod.Post, "/guarded");
            request.Headers.Add("dapr-api-token", presented);
            var response = await app.GetTestClient().SendAsync(request);

            (response.StatusCode == HttpStatusCode.Forbidden).ShouldBe(rejected);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string WriteTokenFile(string content)
    {
        var file = Path.Combine(Path.GetTempPath(), $"app-api-token-{Guid.NewGuid():N}");
        File.WriteAllText(file, content);
        return file;
    }

    private static Dictionary<string, string?> TokenFileConfig(string file) => new()
    {
        ["Workflow:RuntimeCaller:AppApiTokenFile"] = file,
        ["Scripting:Sandbox:Enabled"] = "true"
    };

    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(Substitute.For<IRuntimeInfoProvider>());
        services.AddRuntimeCallerIdentity(configuration, runsFlowScripts: true);
        services.AddDaprLocalDelivery(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class FixedOptionsMonitor(DaprLocalDeliveryOptions value) : IOptionsMonitor<DaprLocalDeliveryOptions>
    {
        public DaprLocalDeliveryOptions CurrentValue => value;
        public DaprLocalDeliveryOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DaprLocalDeliveryOptions, string?> listener) => null;
    }
}

/// <summary>Serialises the tests that touch the process-wide <c>APP_API_TOKEN</c> variable.</summary>
[CollectionDefinition(nameof(DaprLocalDeliveryWiringCollection), DisableParallelization = true)]
public sealed class DaprLocalDeliveryWiringCollection;
