using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Components;
using BBT.Workflow.Components.Dtos;
using BBT.Workflow.Controllers.Components;
using BBT.Workflow.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// Pins the discovery routes that share a shape: <c>{domain}/components/{type}/{key}/versions</c> (the version
/// list) and <c>{domain}/components/{type}/{key}/{cVersion}</c> (one version). The literal segment must win,
/// and a real version must still reach the single-version read.
/// </summary>
public sealed class ComponentDiscoveryRouteTests
{
    [Fact]
    public async Task VersionsSegment_RoutesToTheVersionList()
    {
        var discovery = Substitute.For<IComponentDiscoveryAppService>();
        discovery.GetVersionsAsync("core", ComponentType.Workflows, "flow-a", 2, 5, Arg.Any<CancellationToken>())
            .Returns(Result<ComponentVersionListDto>.Ok(new ComponentVersionListDto
            {
                Type = "workflows",
                ComponentTypeKey = "sys-flows",
                Domain = "core",
                Key = "flow-a",
                TotalVersions = 0,
                Latest = new ComponentLatestResolutionDto { Outcome = "notFound", Source = "automatic" },
                Items = [],
                Page = 2,
                PageSize = 5,
                HasNext = false
            }));
        await using var app = await StartAsync(discovery);

        var response = await app.GetTestClient().GetAsync("/api/v1/core/components/workflows/flow-a/versions?page=2&pageSize=5");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await discovery.Received(1).GetVersionsAsync("core", ComponentType.Workflows, "flow-a", 2, 5, Arg.Any<CancellationToken>());
        await discovery.DidNotReceiveWithAnyArgs().GetAsync(default!, default, default!, default, default);
    }

    [Fact]
    public async Task VersionSegment_StillRoutesToTheSingleVersionRead()
    {
        var discovery = Substitute.For<IComponentDiscoveryAppService>();
        discovery.GetAsync("core", ComponentType.Workflows, "flow-a", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<ComponentDetailDto>.Ok(new ComponentDetailDto
            {
                Type = "workflows",
                Key = "flow-a",
                Domain = "core",
                Version = "1.0.0-pkg.1.0.350+core",
                Definition = new { }
            }));
        await using var app = await StartAsync(discovery);

        var response = await app.GetTestClient().GetAsync("/api/v1/core/components/workflows/flow-a/1.0.0");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await discovery.Received(1).GetAsync("core", ComponentType.Workflows, "flow-a", "1.0.0", Arg.Any<CancellationToken>());
        await discovery.DidNotReceiveWithAnyArgs().GetVersionsAsync(default!, default, default!, default, default, default);
    }

    private static async Task<WebApplication> StartAsync(IComponentDiscoveryAppService discovery)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAetherApiVersioning(apiTitle: "vNext API");
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(ComponentDiscoveryController).Assembly);
        builder.Services.AddSingleton(discovery);
        builder.Services.AddSingleton(Substitute.For<IHeaderService>());
        builder.Services.AddScoped<ResponseHeaderFilter>();

        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }
}
