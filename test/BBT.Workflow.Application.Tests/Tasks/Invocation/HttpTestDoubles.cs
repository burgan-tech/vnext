using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// Shared HTTP test doubles for the in-process HTTP invoker tests
/// (<see cref="LocalHttpTaskInvokerTests"/> and the type-22 regression suite,
/// <c>ExternalHttpTaskInvokerTests</c>). Single-source per the project's testing convention —
/// previously duplicated as private nested classes.
/// </summary>
internal sealed class CapturingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public string? LastRequestedName { get; private set; }
    public HttpClient? LastCreatedClient { get; private set; }

    public HttpClient CreateClient(string name)
    {
        LastRequestedName = name;
        LastCreatedClient = new HttpClient(handler, disposeHandler: false);
        return LastCreatedClient;
    }
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastContentType { get; private set; }
    public Dictionary<string, string> ResponseHeaders { get; } = [];

    public StubHttpMessageHandler(HttpResponseMessage response)
        : this(_ => response)
    {
    }

    public StubHttpMessageHandler(Exception exception)
        : this(_ => throw exception)
    {
    }

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastContentType = request.Content?.Headers.ContentType?.MediaType;

        var response = _responder(request);
        foreach (var header in ResponseHeaders)
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return Task.FromResult(response);
    }
}

/// <summary>
/// Simulates a transport-level failure (e.g. connection refused) without an actual network call.
/// </summary>
internal sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(exception);
}
