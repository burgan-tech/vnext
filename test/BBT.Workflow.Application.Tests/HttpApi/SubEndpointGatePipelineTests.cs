using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Authorization;
using BBT.Workflow.Authorization.SubEndpoints;
using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.Orchestration.Controllers.Instances;
using BBT.Workflow.RuntimeCaller;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using static BBT.Workflow.Authorization.SubEndpoints.RuntimeCallerTestKeys;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// The sub-endpoint gate in the MVC pipeline (vnext#1265): the ingress strip removes the token header
/// on every route whatever the mode, and a refusal happens in the authorization filter — before the
/// body is bound and before the action runs.
/// </summary>
public sealed class SubEndpointGatePipelineTests
{
    private static readonly SubEndpointGrantOptions RuntimeOnly = new() { Role = "$Everyone", Grant = "deny" };

    [Fact]
    public async Task Enforce_ShouldRefuseClient_BeforeModelBinding_AndNeverRunTheAction()
    {
        await using var app = await StartAsync(SubEndpointGateMode.Enforce);
        var domain = NewDomain();

        // Not JSON at all: if binding ran first this would be a 400.
        var response = await app.GetTestClient().PostAsync(
            $"/_tests/sub-gate/{domain}/workflows/child/sub/instances/start",
            new StringContent("{not json", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // The Inbox forwarder re-delivers on exactly this shape: a top-level string `code` (Aether ProblemDetails).
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
        SubEndpointGateProbeController.Calls.ContainsKey(domain).ShouldBeFalse();
    }

    [Fact]
    public async Task Enforce_ShouldAdmitRuntimeCall_AndHideTheTokenFromTheAction()
    {
        await using var app = await StartAsync(SubEndpointGateMode.Enforce);
        var domain = NewDomain();
        var instance = Guid.NewGuid();
        using var request = Post($"/_tests/sub-gate/{domain}/workflows/parent/instances/{instance}/complete");
        request.Headers.Add(RuntimeCallerToken.HeaderName,
            RuntimeCallerToken.Create("k1", Key(1), RuntimeCallTarget.Complete(domain, "parent", instance), "partner",
                RuntimeCallerClasses.Relay, Now));

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        SubEndpointGateProbeController.Calls[domain].ShouldBe("header:absent");
    }

    [Theory]
    [InlineData(SubEndpointGateMode.Off)]
    [InlineData(SubEndpointGateMode.Observe)]
    public async Task OffAndObserve_ShouldRunTheAction_ForAClient(SubEndpointGateMode mode)
    {
        await using var app = await StartAsync(mode);
        var domain = NewDomain();

        var response = await app.GetTestClient().SendAsync(Post($"/_tests/sub-gate/{domain}/workflows/child/sub/instances/start"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        SubEndpointGateProbeController.Calls[domain].ShouldBe("header:absent");
    }

    [Fact]
    public async Task Off_ShouldStillStripTheTokenHeader()
    {
        await using var app = await StartAsync(SubEndpointGateMode.Off);
        var domain = NewDomain();
        using var request = Post($"/_tests/sub-gate/{domain}/workflows/child/sub/instances/start");
        request.Headers.Add(RuntimeCallerToken.HeaderName, "anything-a-client-sent");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        SubEndpointGateProbeController.Calls[domain].ShouldBe("header:absent");
    }

    [Theory]
    [InlineData(nameof(InstanceController.StartSubAsync), RuntimeCallOperations.SubStart)]
    [InlineData(nameof(InstanceController.CompleteSubAsync), RuntimeCallOperations.Complete)]
    public void InstanceController_ShouldGate_BothRuntimeInternalEndpoints(string action, string operation)
    {
        var attribute = typeof(InstanceController).GetMethod(action)!.GetCustomAttribute<SubEndpointGateAttribute>();

        attribute.ShouldNotBeNull();
        attribute.Arguments.ShouldBe([operation]);
    }

    private static string NewDomain() => $"d{Guid.NewGuid():N}";

    private static HttpRequestMessage Post(string path)
        => new(HttpMethod.Post, path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    private static async Task<WebApplication> StartAsync(SubEndpointGateMode mode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(SubEndpointGateProbeController).Assembly);
        builder.Services.AddAetherAspNetCore();
        builder.Services.AddExceptionHandling();
        builder.Services.Configure<SubEndpointGateOptions>(options =>
        {
            options.Mode = mode;
            options.Grants = [RuntimeOnly];
        });
        builder.Services.AddSingleton<IRuntimeCallerVerifier>(Identity(DefaultRing()));
        builder.Services.AddSingleton(Substitute.For<ICallerRoleResolver>());
        builder.Services.AddScoped<ISubEndpointGate, SubEndpointGate>();

        var app = builder.Build();
        app.UseRuntimeCallerTokenStrip();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }
}

/// <summary>Probe endpoints shaped like the two gated runtime-internal routes.</summary>
[ApiController]
[Route("_tests/sub-gate")]
public sealed class SubEndpointGateProbeController : ControllerBase
{
    /// <summary>What the action saw, keyed by the route's domain (tests run in parallel).</summary>
    public static readonly ConcurrentDictionary<string, string> Calls = new();

    /// <summary>Shaped like <c>sub/instances/start</c>.</summary>
    [HttpPost("{domain}/workflows/{workflow}/sub/instances/start")]
    [SubEndpointGate(RuntimeCallOperations.SubStart)]
    public IActionResult Start(string domain, string workflow, [FromBody] JsonElement body) => Record(domain);

    /// <summary>Shaped like <c>instances/{instance}/complete</c>.</summary>
    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/complete")]
    [SubEndpointGate(RuntimeCallOperations.Complete)]
    public IActionResult Complete(string domain, string workflow, string instance, [FromBody] JsonElement body)
        => Record(domain);

    private IActionResult Record(string domain)
    {
        Calls[domain] = Request.Headers.ContainsKey(RuntimeCallerToken.HeaderName) ? "header:present" : "header:absent";
        return Ok();
    }
}
