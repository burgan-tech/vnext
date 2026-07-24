using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Controllers.Instances;
using BBT.Workflow.Orchestration.Controllers.Instances;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

public sealed class FormUrlEncodedMvcContractTests
{
    [Fact]
    public async Task MvcBinding_FormAndJsonBodies_ProduceEquivalentTypedPayloads()
    {
        await using var app = await StartProbeApplicationAsync();
        var client = app.GetTestClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["attributes[age]"] = "30",
            ["attributes[active]"] = "true"
        });

        var formResponse = await client.PostAsync("/_tests/form-payload", form);
        var jsonResponse = await client.PostAsJsonAsync(
            "/_tests/form-payload",
            new { attributes = new { age = 30, active = true } });

        formResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        jsonResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var formJson = JsonDocument.Parse(await formResponse.Content.ReadAsStringAsync());
        var jsonJson = JsonDocument.Parse(await jsonResponse.Content.ReadAsStringAsync());
        JsonElement.DeepEquals(formJson.RootElement, jsonJson.RootElement).ShouldBeTrue();
    }

    [Fact]
    public async Task MvcBinding_AmbiguousFormPath_ReturnsBadRequest()
    {
        await using var app = await StartProbeApplicationAsync();
        var client = app.GetTestClient();
        using var content = new StringContent(
            "items[][name]=A",
            Encoding.UTF8,
            "application/x-www-form-urlencoded");

        var response = await client.PostAsync("/_tests/form-payload", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.ShouldContain("Empty brackets are supported only for trailing scalar arrays.");
    }

    [Theory]
    [InlineData(nameof(InstanceController), nameof(InstanceController.StartAsync))]
    [InlineData(nameof(InstanceController), nameof(InstanceController.TransitionAsync))]
    [InlineData(nameof(FunctionController), nameof(FunctionController.InvokeDomainFunctionAsync))]
    [InlineData(nameof(FunctionController), nameof(FunctionController.InvokeInstanceFunctionAsync))]
    public async Task ApiMetadata_JsonElementEndpoints_AdvertiseJsonAndForm(
        string controllerName,
        string actionName)
    {
        var mvcActionName = actionName.EndsWith("Async", StringComparison.Ordinal)
            ? actionName[..^"Async".Length]
            : actionName;
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddAetherApiVersioning(apiTitle: "vNext API");
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(InstanceController).Assembly);
        builder.Services.AddFormUrlEncodedJsonElementInput();
        await using var app = builder.Build();

        var descriptions = app.Services
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Where(description =>
            {
                var action = description.ActionDescriptor as ControllerActionDescriptor;
                return action?.ControllerName == controllerName.Replace("Controller", string.Empty) &&
                       action.ActionName == mvcActionName;
            })
            .ToArray();

        descriptions.ShouldNotBeEmpty();
        foreach (var description in descriptions)
        {
            var mediaTypes = description.SupportedRequestFormats
                .Select(format => format.MediaType)
                .ToArray();
            mediaTypes.ShouldContain("application/json");
            mediaTypes.ShouldContain("application/x-www-form-urlencoded");
        }
    }

    private static async Task<WebApplication> StartProbeApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(FormPayloadProbeController).Assembly);
        builder.Services.AddFormUrlEncodedJsonElementInput();
        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }
}

[ApiController]
[Route("_tests/form-payload")]
public sealed class FormPayloadProbeController : ControllerBase
{
    [HttpPost]
    public IActionResult Post([FromBody] JsonElement? body)
        => Ok(body);
}
