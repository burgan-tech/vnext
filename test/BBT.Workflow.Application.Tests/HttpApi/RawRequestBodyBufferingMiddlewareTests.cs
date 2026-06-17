using System.IO;
using System.Text;
using System.Threading.Tasks;
using BBT.Workflow.Middlewares;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// Unit tests for <see cref="RawRequestBodyBufferingMiddleware"/>: captures the raw body for
/// body-bearing requests and rewinds the stream so downstream model binding still works.
/// </summary>
public class RawRequestBodyBufferingMiddlewareTests
{
    private const string Body = "{\n  \"Amount\":100  }"; // intentionally non-normalized

    private static DefaultHttpContext BuildContext(string method, string? body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
        }

        return context;
    }

    [Fact]
    public async Task InvokeAsync_PostBody_CapturesRawAndRewindsStream()
    {
        var context = BuildContext("POST", Body);
        string? downstreamBody = null;

        var middleware = new RawRequestBodyBufferingMiddleware(async ctx =>
        {
            // Simulate model binding reading the (rewound) body.
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            downstreamBody = await reader.ReadToEndAsync();
        });

        await middleware.InvokeAsync(context);

        context.Items[RawRequestBodyBufferingMiddleware.RawBodyItemsKey].ShouldBe(Body);
        downstreamBody.ShouldBe(Body); // stream was rewound for downstream binding
    }

    [Fact]
    public async Task InvokeAsync_GetRequest_DoesNotCapture()
    {
        var context = BuildContext("GET", null);

        var middleware = new RawRequestBodyBufferingMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context);

        context.Items.ContainsKey(RawRequestBodyBufferingMiddleware.RawBodyItemsKey).ShouldBeFalse();
    }
}
