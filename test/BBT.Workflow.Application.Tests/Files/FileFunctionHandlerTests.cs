using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.AspNetCore.ExceptionHandling;
using BBT.Aether.ExceptionHandling;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Controllers.Instances;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Files;

/// <summary>
/// Covers <see cref="FileFunctionHandler"/> through a real (TestServer) HTTP pipeline so Range, ETag and HEAD are
/// executed by the framework's file executor rather than asserted on a result object.
/// </summary>
public sealed class FileFunctionHandlerTests
{
    private static readonly byte[] Bytes = [1, 2, 3];
    private readonly IInstanceFileAppService _files = Substitute.For<IInstanceFileAppService>();

    private static FileHandle Handle(string? name = "kimlik-ön.pdf", string? mime = "application/pdf") =>
        new("vnext-blob-local", "11111111-1111-1111-1111-111111111111", name, mime, 3, "abc123",
            new FileOwner("core", "kyc", "i1"));

    private void Returns(Result<InstanceFileContent> r) =>
        _files.ReadAsync(Arg.Any<InstanceFileRequest>(), Arg.Any<CancellationToken>()).Returns(r);

    private void ReturnsContent(bool notModified = false, FileHandle? handle = null) =>
        Returns(Result<InstanceFileContent>.Ok(new InstanceFileContent(handle ?? Handle(), "passport", notModified ? null : Bytes, notModified)));

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers();
        builder.Services.AddTransient<IHttpExceptionStatusCodeFinder, DefaultHttpExceptionStatusCodeFinder>();
        builder.Services.AddSingleton<IProblemDetailsFactory, ProblemDetailsFactory>();
        builder.Services.Configure<AetherExceptionHandlingOptions>(_ => { });
        var app = builder.Build();
        var handler = new FileFunctionHandler(_files);
        app.Run(async ctx =>
        {
            var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => (string?)h.Value.ToString());
            var query = ctx.Request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString());
            var request = new InstanceFunctionRequest(
                "core", "kyc", "i1", new FunctionQueryParameters(),
                ctx.Request.Headers.IfNoneMatch.FirstOrDefault(), headers, query,
                Substitute.For<ICurrentUser>(), ctx);
            var result = await handler.HandleAsync(request, ctx.RequestAborted);
            await result.ExecuteResultAsync(new ActionContext(ctx, new RouteData(), new ActionDescriptor()));
        });
        await app.StartAsync();
        return app;
    }

    private const string Url = "/f?file=11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Get_ReturnsBytesWithSafeHeaders()
    {
        ReturnsContent();
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync(Url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Bytes);
        response.Headers.ETag!.Tag.ShouldBe("\"abc123\"");
        response.Headers.AcceptRanges.ShouldContain("bytes");
        var cache = response.Headers.CacheControl!;
        cache.Private.ShouldBeTrue();
        cache.MaxAge.ShouldBe(TimeSpan.FromSeconds(31536000));
        cache.ToString().ShouldContain("immutable");
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        var disposition = response.Content.Headers.ContentDisposition!.ToString();
        disposition.ShouldStartWith("inline");
        disposition.ShouldContain("filename*=UTF-8''kimlik-%C3%B6n.pdf");
        response.Content.Headers.ContentEncoding.ShouldBe(["identity"]);
    }

    // ── I2: client-declared media types never run as documents of this origin ───────────────────────────────

    [Theory]
    [InlineData("text/html", "attachment")]
    [InlineData("text/html; charset=utf-8", "attachment")]
    [InlineData("image/svg+xml", "attachment")]
    [InlineData("application/xhtml+xml", "attachment")]
    [InlineData("application/xml", "attachment")]
    [InlineData("text/xml", "attachment")]
    [InlineData("application/javascript", "attachment")]
    [InlineData("text/javascript", "attachment")]
    [InlineData("application/rss+xml", "attachment")]
    [InlineData("application/pdf", "inline")]
    [InlineData("image/png", "inline")]
    public async Task Get_DispositionFollowsTheMediaType_AndAlwaysCarriesTheSandboxCsp(string mime, string disposition)
    {
        ReturnsContent(handle: Handle(mime: mime));
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync(Url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe(disposition);
        response.Headers.GetValues("Content-Security-Policy").ShouldBe(["sandbox; default-src 'none'"]);
    }

    [Theory]
    [InlineData("not a media type")]
    [InlineData("text/html\r\nX-Injected: 1")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Get_UnparseableStoredMimeType_IsServedAsOctetStream(string? mime)
    {
        ReturnsContent(handle: Handle(mime: mime));
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync(Url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        response.Headers.GetValues("Content-Security-Policy").ShouldBe(["sandbox; default-src 'none'"]);
    }

    [Theory]
    [InlineData("a\"b.pdf")]
    [InlineData("evil\r\nSet-Cookie: x=1.pdf")]
    public async Task Get_QuoteOrCrlfInTheName_StaysInsideTheDispositionHeader(string name)
    {
        ReturnsContent(handle: Handle(name: name));
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync(Url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        var header = response.Content.Headers.GetValues("Content-Disposition").Single();
        header.ShouldNotContain("\r");
        header.ShouldNotContain("\n");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
    }

    [Fact]
    public async Task Get_LongName_IsServedAsStored()
    {
        // The write path caps names at 255 characters (FileOffloadService.NormalizeName); the header carries it whole.
        var name = new string('a', 251) + ".pdf";
        ReturnsContent(handle: Handle(name: name));
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync(Url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.ToString().ShouldContain(name);
    }

    [Fact]
    public async Task Get_IfNoneMatch_Returns304WithETag()
    {
        ReturnsContent(notModified: true);
        await using var app = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"abc123\""));

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        response.Headers.ETag!.Tag.ShouldBe("\"abc123\"");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_Range_Returns206WithSlice()
    {
        ReturnsContent();
        await using var app = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Range = new RangeHeaderValue(0, 1);

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe([1, 2]);
        response.Content.Headers.ContentRange!.ToString().ShouldBe("bytes 0-1/3");
    }

    [Fact]
    public async Task Get_RangeBeyondLength_Returns416()
    {
        ReturnsContent();
        await using var app = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Range = new RangeHeaderValue(10, 20);

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    [Fact]
    public async Task Head_ReturnsHeadersAndLengthWithoutBody()
    {
        ReturnsContent();
        await using var app = await StartAsync();

        var response = await app.GetTestClient().SendAsync(new HttpRequestMessage(HttpMethod.Head, Url));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.ShouldBe(3);
        response.Headers.ETag!.Tag.ShouldBe("\"abc123\"");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_MissingFileParameter_Returns400WithoutReadingTheStore()
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync("/f");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _files.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    public async Task Get_FileNotFound_Returns404()
    {
        Returns(Result<InstanceFileContent>.Fail(WorkflowErrors.FileNotFound("x")));
        await using var app = await StartAsync();

        (await app.GetTestClient().GetAsync(Url)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_QueryRolesDenied_Returns403()
    {
        Returns(Result<InstanceFileContent>.Fail(WorkflowErrors.QueryAccessDenied("s1")));
        await using var app = await StartAsync();

        (await app.GetTestClient().GetAsync(Url)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_StoreUnavailable_Returns503()
    {
        Returns(Result<InstanceFileContent>.Fail(WorkflowErrors.FileStoreUnavailable("vnext-blob-local")));
        await using var app = await StartAsync();

        (await app.GetTestClient().GetAsync(Url)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Get_PassesFileRoute_IfNoneMatchAndAuthorizationToTheAppService()
    {
        ReturnsContent();
        await using var app = await StartAsync();

        await app.GetTestClient().GetAsync(Url);

        await _files.Received(1).ReadAsync(
            Arg.Is<InstanceFileRequest>(r => r.Domain == "core" && r.Flow == "kyc" && r.Instance == "i1"
                && r.File == "11111111-1111-1111-1111-111111111111" && r.Authorization != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void FunctionController_HasADedicatedHeadActionOnTheFileRoute()
    {
        var method = typeof(FunctionController)
            .GetMethod("HeadFileFunctionAsync")!;
        var head = method.GetCustomAttributes<HttpHeadAttribute>().Single();
        head.Template.ShouldEndWith("/functions/file");
    }

    [Fact]
    public void Handler_IsKeyedByTheFileFunctionType()
    {
        new FileFunctionHandler(_files).FunctionType.ShouldBe("file");
    }
}
