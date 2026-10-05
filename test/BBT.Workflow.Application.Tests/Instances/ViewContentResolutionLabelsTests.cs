using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// The view response carries the view component's <c>labels</c> in the definition's own
/// <c>[{ language, label }]</c> form, whether the view resolves locally or from another domain, and
/// omits the field when the component declares none.
/// </summary>
public class ViewContentResolutionLabelsTests
{
    private const string RequestDomain = "core";

    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IInstanceQueryGateway _instanceQueryGateway = Substitute.For<IInstanceQueryGateway>();
    private readonly ViewContentResolutionService _sut;

    public ViewContentResolutionLabelsTests()
    {
        _sut = new ViewContentResolutionService(
            _componentCacheStore,
            _instanceQueryGateway,
            Substitute.For<IRuntimeInfoProvider>(),
            NullLogger<ViewContentResolutionService>.Instance);
    }

    [Fact]
    public async Task ResolveViewContentAsync_LocalView_CarriesTheComponentsLabels()
    {
        var view = JsonSerializer.Deserialize<View>(
            """{"type": 1, "content": "{}", "display": "full-page", "labels": [{"label":"Onay","language":"tr-TR"},{"label":"Approval","language":"en-US"}]}""",
            JsonSerializerConstants.JsonOptions)!;
        _componentCacheStore
            .GetViewAsync(RequestDomain, "approval-view", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<View>.Ok(view));

        var result = await _sut.ResolveViewContentAsync(
            new Reference("approval-view", RequestDomain, "sys-views", "1.0.0"), RequestDomain, null, null);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Labels!.Select(l => (l.Language, l.Label))
            .ShouldBe([("tr-TR", "Onay"), ("en-US", "Approval")]);
    }

    [Fact]
    public async Task ResolveViewContentAsync_LocalViewWithoutLabels_OmitsThem()
    {
        var view = JsonSerializer.Deserialize<View>(
            """{"type": 1, "content": "{}", "display": "full-page"}""", JsonSerializerConstants.JsonOptions)!;
        _componentCacheStore
            .GetViewAsync(RequestDomain, "approval-view", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<View>.Ok(view));

        var result = await _sut.ResolveViewContentAsync(
            new Reference("approval-view", RequestDomain, "sys-views", "1.0.0"), RequestDomain, null, null);

        result.Value!.Labels.ShouldBeNull();
        JsonSerializer.Serialize(result.Value, JsonSerializerConstants.JsonOptions).ShouldNotContain("labels");
    }

    [Fact]
    public async Task ResolveViewContentAsync_RemoteView_CarriesTheLabelsFromItsAttributes()
    {
        var attributes = JsonDocument.Parse(
            """{"type": 1, "content": "{}", "display": "full-page", "labels": [{"label":"Onay","language":"tr-TR"}]}""")
            .RootElement.Clone();
        _instanceQueryGateway
            .GetInstanceAsync(Arg.Any<GetInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceOutput>.Success(new GetInstanceOutput
            {
                Key = "approval-view",
                Attributes = attributes
            }));

        var result = await _sut.ResolveViewContentAsync(
            new Reference("approval-view", "partner", "sys-views", "1.0.0"), RequestDomain, null, null);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Labels!.Single().Label.ShouldBe("Onay");
        result.Value.Labels!.Single().Language.ShouldBe("tr-TR");
    }
}
