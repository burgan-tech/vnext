using BBT.Workflow.Middlewares;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting;

/// <summary>
/// Unit tests for <see cref="HttpContextRawBodyProvider"/> resolution precedence:
/// ambient job scope first, then the live HTTP request, then null.
/// </summary>
public class HttpContextRawBodyProviderTests
{
    [Fact]
    public void GetRawBody_ReturnsHttpContextItem_WhenPresent()
    {
        var context = new DefaultHttpContext();
        context.Items[RawRequestBodyBufferingMiddleware.RawBodyItemsKey] = "LIVE";
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);

        var provider = new HttpContextRawBodyProvider(accessor);

        provider.GetRawBody().ShouldBe("LIVE");
    }

    [Fact]
    public void GetRawBody_PrefersAmbientScope_OverHttpContext()
    {
        // Inside a background job the surrounding HTTP request is the Dapr transport, not the payload.
        var context = new DefaultHttpContext();
        context.Items[RawRequestBodyBufferingMiddleware.RawBodyItemsKey] = "DAPR-TRANSPORT";
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);

        var provider = new HttpContextRawBodyProvider(accessor);

        using (BBT.Workflow.Scripting.RawBodyExecutionScope.Set("ORIGINAL"))
        {
            provider.GetRawBody().ShouldBe("ORIGINAL");
        }
    }

    [Fact]
    public void GetRawBody_ReturnsNull_WhenNoHttpContextAndNoScope()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);

        var provider = new HttpContextRawBodyProvider(accessor);

        provider.GetRawBody().ShouldBeNull();
    }

    private static readonly System.Text.Json.JsonElement Swapped =
        System.Text.Json.JsonDocument.Parse("{\"doc\":{\"file\":\"6f1c\"}}").RootElement.Clone();

    private static (HttpContextRawBodyProvider Provider, DefaultHttpContext Context) WithCapturedBody(
        object captured, string? payloadMode = null)
    {
        var context = new DefaultHttpContext();
        context.Items[RawRequestBodyBufferingMiddleware.RawBodyItemsKey] = captured;
        if (payloadMode is not null)
            context.Request.Headers[BBT.Workflow.Payloads.PayloadEnvelope.ModeHeaderName] = payloadMode;
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return (new HttpContextRawBodyProvider(accessor), context);
    }

    [Fact]
    public void ReplaceRawBodyAttributes_StandardEnvelope_RewritesOnlyAttributes()
    {
        // x-storage admission: scripts must see the swapped handles, never the file bytes — and the
        // envelope's key/tags/stage must survive.
        var (provider, _) = WithCapturedBody(
            "{\"key\":\"K1\",\"tags\":[\"a\"],\"attributes\":{\"doc\":{\"content\":\"AAEC\"}}}");

        provider.ReplaceRawBodyAttributes(Swapped);

        provider.GetRawBody().ShouldBe("{\"key\":\"K1\",\"tags\":[\"a\"],\"attributes\":{\"doc\":{\"file\":\"6f1c\"}}}");
    }

    [Fact]
    public void ReplaceRawBodyAttributes_LazyCapture_IsReadAndRewritten()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"attributes\":{\"doc\":{\"content\":\"AAEC\"}},\"stage\":\"s\"}");
        var (provider, _) = WithCapturedBody(new RawRequestBodyCapture(bytes, bytes.Length));

        provider.ReplaceRawBodyAttributes(Swapped);

        provider.GetRawBody().ShouldBe("{\"attributes\":{\"doc\":{\"file\":\"6f1c\"}},\"stage\":\"s\"}");
    }

    [Fact]
    public void ReplaceRawBodyAttributes_FreeFormBody_IsReplacedWhole()
    {
        var (provider, _) = WithCapturedBody("{\"doc\":{\"content\":\"AAEC\"}}");

        provider.ReplaceRawBodyAttributes(Swapped);

        provider.GetRawBody().ShouldBe(Swapped.GetRawText());
    }

    [Fact]
    public void ReplaceRawBodyAttributes_RawModeHeader_ReplacesWholeEvenWhenShapedLikeAnEnvelope()
    {
        // x-vnext-payload-mode: raw wins over shape detection, exactly as in the controllers.
        var (provider, _) = WithCapturedBody("{\"attributes\":{\"content\":\"AAEC\"}}", payloadMode: "raw");

        provider.ReplaceRawBodyAttributes(Swapped);

        provider.GetRawBody().ShouldBe(Swapped.GetRawText());
    }

    [Fact]
    public void ReplaceRawBodyAttributes_WithoutACapturedBody_AddsNothing()
    {
        var context = new DefaultHttpContext();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);

        new HttpContextRawBodyProvider(accessor).ReplaceRawBodyAttributes(Swapped);

        context.Items.ContainsKey(RawRequestBodyBufferingMiddleware.RawBodyItemsKey).ShouldBeFalse();
    }
}
