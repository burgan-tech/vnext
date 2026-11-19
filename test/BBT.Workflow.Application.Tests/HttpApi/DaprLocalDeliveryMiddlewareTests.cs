using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

public sealed class DaprLocalDeliveryMiddlewareTests
{
    private const string Token = "s3cret";

    private static Endpoint Guarded() =>
        new(_ => Task.CompletedTask, new EndpointMetadataCollection(new DaprLocalDeliveryOnlyAttribute()), "x");

    private static Endpoint JobRoute() =>
        new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/job/{jobName}"), 0,
            EndpointMetadataCollection.Empty, "job");

    private static Endpoint Plain() => new(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "plain");

    private sealed class Harness
    {
        public bool NextCalled;
        public List<string> SeenHeaders = [];
        public readonly CapturingLogger Logger = new();
        public DefaultHttpContext Context = new();
        public DaprLocalDeliveryMiddleware Middleware;

        public Harness(DaprLocalDeliveryMode mode, string? token = Token)
        {
            Middleware = new DaprLocalDeliveryMiddleware(
                ctx =>
                {
                    NextCalled = true;
                    SeenHeaders = ctx.Request.Headers.Keys.ToList();
                    return Task.CompletedTask;
                },
                new TestOptionsMonitor(new DaprLocalDeliveryOptions { Mode = mode }),
                new DaprAppApiToken(token),
                Logger);
        }

        public Task Run(Endpoint? endpoint, string? token = null, string? caller = null, string path = "/x")
        {
            Context = new DefaultHttpContext();
            Context.Request.Path = path;
            if (token != null) Context.Request.Headers[DaprLocalDeliveryEvaluator.TokenHeader] = token;
            if (caller != null) Context.Request.Headers[DaprLocalDeliveryEvaluator.CallerAppIdHeader] = caller;
            if (endpoint != null) Context.SetEndpoint(endpoint);
            return Middleware.InvokeAsync(Context);
        }
    }

    [Fact]
    public async Task Enforce_GuardedEndpoint_ValidToken_CallsNext_AndStripsToken()
    {
        var h = new Harness(DaprLocalDeliveryMode.Enforce);
        await h.Run(Guarded(), Token);
        h.NextCalled.ShouldBeTrue();
        h.SeenHeaders.ShouldNotContain("dapr-api-token");
    }

    [Fact]
    public async Task Enforce_GuardedEndpoint_CallerHeader_Returns404_DoesNotCallNext()
    {
        var h = new Harness(DaprLocalDeliveryMode.Enforce);
        await h.Run(Guarded(), Token, caller: "other");
        h.Context.Response.StatusCode.ShouldBe(404);
        h.NextCalled.ShouldBeFalse();
        h.Context.Request.Headers.ContainsKey("dapr-api-token").ShouldBeFalse();
    }

    [Fact]
    public async Task Enforce_JobRouteEndpoint_NoToken_Returns404()
    {
        var h = new Harness(DaprLocalDeliveryMode.Enforce);
        await h.Run(JobRoute());
        h.Context.Response.StatusCode.ShouldBe(404);
        h.NextCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Enforce_TokenNotConfigured_Returns404()
    {
        var h = new Harness(DaprLocalDeliveryMode.Enforce, token: null);
        await h.Run(Guarded(), Token);
        h.Context.Response.StatusCode.ShouldBe(404);
        h.NextCalled.ShouldBeFalse();
    }

    [Fact]
    public void Enforce_NoToken_LogsMisconfigurationOnce_AtConstruction()
    {
        var h = new Harness(DaprLocalDeliveryMode.Enforce, token: null);
        h.Logger.EventIds.Count(i => i == 40997).ShouldBe(1);
    }

    [Fact]
    public async Task Rejection_Is404_WithEmptyBody()
    {
        var h = new Harness(DaprLocalDeliveryMode.Enforce);
        await h.Run(Guarded());
        h.Context.Response.ContentLength.ShouldBeNull();
        h.Context.Response.Body.Length.ShouldBe(0);
    }

    [Fact]
    public async Task LogOnly_GuardedEndpoint_NoToken_CallsNext_StripsHeader_NoPerRequestLog()
    {
        var h = new Harness(DaprLocalDeliveryMode.LogOnly, token: null);
        await h.Run(Guarded(), Token);
        await h.Run(Guarded(), Token);
        h.NextCalled.ShouldBeTrue();
        h.SeenHeaders.ShouldNotContain("dapr-api-token");
        h.Logger.EventIds.ShouldNotContain(40996);
        h.Logger.EventIds.Count(i => i == 40998).ShouldBe(1);
    }

    [Fact]
    public void LogOnly_TokenConfigured_DoesNotLog40998()
    {
        var h = new Harness(DaprLocalDeliveryMode.LogOnly);
        h.Logger.EventIds.ShouldNotContain(40998);
    }

    [Fact]
    public async Task LogOnly_TokenConfigured_MissingToken_CallsNext_AndLogs40996()
    {
        var h = new Harness(DaprLocalDeliveryMode.LogOnly);
        await h.Run(Guarded());
        h.NextCalled.ShouldBeTrue();
        h.Logger.EventIds.Count(i => i == 40996).ShouldBe(1);
        h.Logger.Messages.Single(m => m.Contains("LogOnly")).ShouldContain("would reject");
    }

    [Fact]
    public async Task LogOnly_TokenConfigured_InvalidToken_Logs40996()
    {
        var h = new Harness(DaprLocalDeliveryMode.LogOnly);
        await h.Run(Guarded(), Token + "-wrong");
        h.NextCalled.ShouldBeTrue();
        h.Logger.EventIds.Count(i => i == 40996).ShouldBe(1);
    }

    [Fact]
    public async Task Enforce_TokenNotConfigured_Logs40996_PerRequest()
    {
        var h = new Harness(DaprLocalDeliveryMode.Enforce, token: null);
        await h.Run(Guarded(), Token);
        h.Logger.EventIds.Count(i => i == 40996).ShouldBe(1);
    }

    [Fact]
    public async Task LogOnly_GuardedEndpoint_ValidToken_StripsToken()
    {
        var h = new Harness(DaprLocalDeliveryMode.LogOnly);
        await h.Run(Guarded(), Token);
        h.NextCalled.ShouldBeTrue();
        h.SeenHeaders.ShouldNotContain("dapr-api-token");
        h.Logger.EventIds.ShouldNotContain(40996);
    }

    [Fact]
    public async Task Off_GuardedEndpoint_NoToken_CallsNext_NoLog()
    {
        var h = new Harness(DaprLocalDeliveryMode.Off);
        await h.Run(Guarded());
        h.NextCalled.ShouldBeTrue();
        h.Logger.EventIds.ShouldNotContain(40996);
    }

    [Fact]
    public async Task UnguardedEndpoint_TokenPresent_StripsToken_CallsNext()
    {
        var h = new Harness(DaprLocalDeliveryMode.Enforce);
        await h.Run(Plain(), Token);
        h.NextCalled.ShouldBeTrue();
        h.SeenHeaders.ShouldNotContain("dapr-api-token");
    }

    [Fact]
    public async Task NoEndpoint_TokenPresent_StripsToken_CallsNext()
    {
        var h = new Harness(DaprLocalDeliveryMode.Enforce);
        await h.Run(null, Token);
        h.NextCalled.ShouldBeTrue();
        h.SeenHeaders.ShouldNotContain("dapr-api-token");
    }

    [Theory]
    [InlineData("/api/v1/core/workflows/a/instances/start")]
    [InlineData("/api/v1/sales/workflows/a/instances/start")]
    public async Task CoHostedDomains_SameRuleForBothDomainRoutes(string path)
    {
        var h = new Harness(DaprLocalDeliveryMode.Enforce);
        await h.Run(Guarded(), path: path);
        h.Context.Response.StatusCode.ShouldBe(404);

        var ok = new Harness(DaprLocalDeliveryMode.Enforce);
        await ok.Run(Guarded(), Token, path: path);
        ok.NextCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task Rejection_LogNeverContainsToken()
    {
        var h = new Harness(DaprLocalDeliveryMode.Enforce);
        await h.Run(Guarded(), Token + "-wrong", caller: "evil");
        h.Logger.EventIds.ShouldContain(40996);
        h.Logger.Messages.ShouldAllBe(m => !m.Contains(Token));
        h.Logger.Messages.ShouldContain(m => m.Contains("evil"));
    }

    private sealed class TestOptionsMonitor(DaprLocalDeliveryOptions value) : IOptionsMonitor<DaprLocalDeliveryOptions>
    {
        public DaprLocalDeliveryOptions CurrentValue => value;
        public DaprLocalDeliveryOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DaprLocalDeliveryOptions, string?> listener) => null;
    }

    private sealed class CapturingLogger : ILogger<DaprLocalDeliveryMiddleware>
    {
        private readonly List<int> _eventIds = [];
        private readonly List<string> _messages = [];
        public IReadOnlyList<int> EventIds => _eventIds;
        public IReadOnlyList<string> Messages => _messages;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _eventIds.Add(eventId.Id);
            _messages.Add(formatter(state, exception));
        }
    }
}
