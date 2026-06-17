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
}
