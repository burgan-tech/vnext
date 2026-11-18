using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BBT.Workflow.Orchestration.Controllers.Instances;
using BBT.Workflow.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            new FixedOptionsMonitor(new DaprLocalDeliveryOptions
            {
                Mode = DaprLocalDeliveryMode.Enforce,
                AppApiToken = "s3cret"
            }),
            NullLogger<DaprLocalDeliveryMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.SetEndpoint(job);
        await middleware.InvokeAsync(context); // no token: a guarded endpoint must be rejected

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        nextCalled.ShouldBeFalse();
        job.RoutePattern.RawText.ShouldBe("/job/{jobName}");
    }

    [Fact]
    public void Options_BindFromConfiguration()
    {
        var provider = Build(new Dictionary<string, string?>
        {
            ["Dapr:LocalDelivery:Mode"] = "Enforce",
            ["Dapr:LocalDelivery:AppApiToken"] = "from-config"
        });

        var options = provider.GetRequiredService<IOptions<DaprLocalDeliveryOptions>>().Value;
        options.Mode.ShouldBe(DaprLocalDeliveryMode.Enforce);
        options.AppApiToken.ShouldBe("from-config");
    }

    [Fact]
    public void Options_FallBackToAppApiTokenEnvironmentVariable()
    {
        var previous = Environment.GetEnvironmentVariable(EnvName);
        try
        {
            Environment.SetEnvironmentVariable(EnvName, "from-env");

            var provider = Build(new Dictionary<string, string?> { ["Dapr:LocalDelivery:Mode"] = "LogOnly" });
            provider.GetRequiredService<IOptions<DaprLocalDeliveryOptions>>().Value.AppApiToken.ShouldBe("from-env");

            // A configured token wins over the environment variable.
            var configured = Build(new Dictionary<string, string?> { ["Dapr:LocalDelivery:AppApiToken"] = "from-config" });
            configured.GetRequiredService<IOptions<DaprLocalDeliveryOptions>>().Value.AppApiToken.ShouldBe("from-config");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, previous);
        }
    }

    [Fact]
    public void Options_WithoutConfigOrEnvironment_LeaveTokenUnset_AndDefaultToLogOnly()
    {
        var previous = Environment.GetEnvironmentVariable(EnvName);
        try
        {
            Environment.SetEnvironmentVariable(EnvName, null);

            var options = Build(new Dictionary<string, string?>()).GetRequiredService<IOptions<DaprLocalDeliveryOptions>>().Value;
            options.AppApiToken.ShouldBeNullOrWhiteSpace();
            options.Mode.ShouldBe(DaprLocalDeliveryMode.LogOnly);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, previous);
        }
    }

    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
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
