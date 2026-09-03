using System.Collections.Generic;
using System.Net.Http;
using BBT.Workflow.Remote;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

/// <summary>
/// Pins the W3C trace-context guard on the cross-domain forward path. Both header sources merged by
/// <see cref="CurrentUserForwardHeadersHelper.MergeIntoRequest"/> are raw captured inbound request
/// headers — <c>forwardHeaders</c> from <c>ICurrentUser.ToForwardHeaders()</c>, <c>inputHeaders</c>
/// from the transition input, possibly restored from a persisted job payload minutes later. Before the
/// guard, a captured <c>traceparent</c> was copied onto the outbound request; because .NET's
/// DiagnosticsHandler injects <c>traceparent</c> fill-if-absent, the stale copy won and the callee was
/// parented to a span from a different request instead of the live Activity. The guard must be
/// unconditional (not routed through the optional <c>isRestrictedHeader</c> callback) because several
/// callers pass no callback at all.
/// </summary>
public sealed class CurrentUserForwardHeadersHelperTraceHeaderTests
{
    [Theory]
    [InlineData("traceparent")]
    [InlineData("TraceParent")]
    [InlineData("tracestate")]
    [InlineData("baggage")]
    public void MergeIntoRequest_TraceContextHeaderInForwardHeaders_IsNotCopied(string headerName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://x/");
        var forwardHeaders = new Dictionary<string, string?>
        {
            [headerName] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["X-Custom"] = "kept"
        };

        CurrentUserForwardHeadersHelper.MergeIntoRequest(request, forwardHeaders, inputHeaders: null);

        request.Headers.Contains(headerName).ShouldBeFalse();
        request.Headers.Contains("X-Custom").ShouldBeTrue();
    }

    [Theory]
    [InlineData("traceparent")]
    [InlineData("TraceParent")]
    [InlineData("tracestate")]
    [InlineData("baggage")]
    public void MergeIntoRequest_TraceContextHeaderInInputHeaders_IsNotCopiedAndDoesNotThrow(string headerName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://x/");
        var forwardHeaders = new Dictionary<string, string?>();
        var inputHeaders = new Dictionary<string, string?>
        {
            [headerName] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
        };

        Should.NotThrow(() =>
            CurrentUserForwardHeadersHelper.MergeIntoRequest(request, forwardHeaders, inputHeaders));

        request.Headers.Contains(headerName).ShouldBeFalse();
    }

    [Fact]
    public void MergeIntoRequest_TraceContextGuardIsUnconditional_EvenWhenNoRestrictedCallbackIsPassed()
    {
        // Callers such as RemoteInstanceQueryAppService / RemoteAuthorizeAppService pass no callback,
        // so the skip must not depend on RemoteHttpResponseHelper.IsRestrictedHeader being wired in.
        var request = new HttpRequestMessage(HttpMethod.Post, "http://x/");
        var forwardHeaders = new Dictionary<string, string?> { ["traceparent"] = "stale" };
        var inputHeaders = new Dictionary<string, string?> { ["tracestate"] = "stale", ["baggage"] = "stale" };

        CurrentUserForwardHeadersHelper.MergeIntoRequest(request, forwardHeaders, inputHeaders, isRestrictedHeader: null);

        request.Headers.Contains("traceparent").ShouldBeFalse();
        request.Headers.Contains("tracestate").ShouldBeFalse();
        request.Headers.Contains("baggage").ShouldBeFalse();
    }

    [Fact]
    public void MergeIntoRequest_NonTraceHeadersInInputHeaders_AreStillCopied()
    {
        // The task-invoker guard (HttpTaskInvocation.ReservedTraceHeaders) also drops x-request-id;
        // this path must NOT — correlation ids are legitimately forwarded cross-domain.
        var request = new HttpRequestMessage(HttpMethod.Post, "http://x/");
        var forwardHeaders = new Dictionary<string, string?>();
        var inputHeaders = new Dictionary<string, string?>
        {
            ["X-Custom"] = "custom-value",
            ["x-request-id"] = "req-123"
        };

        CurrentUserForwardHeadersHelper.MergeIntoRequest(request, forwardHeaders, inputHeaders);

        request.Headers.Contains("X-Custom").ShouldBeTrue();
        request.Headers.Contains("x-request-id").ShouldBeTrue();
        request.Headers.GetValues("x-request-id").ShouldBe(["req-123"]);
    }

    [Theory]
    [InlineData("traceparent")]
    [InlineData("Baggage")]
    [InlineData("TRACESTATE")]
    public void IsRestrictedHeader_TraceContextHeaders_AreRestricted(string headerName)
    {
        RemoteHttpResponseHelper.IsRestrictedHeader(headerName).ShouldBeTrue();
    }

    [Theory]
    [InlineData("X-Custom")]
    [InlineData("x-request-id")]
    [InlineData("Authorization")]
    public void IsRestrictedHeader_OrdinaryHeaders_AreNotRestricted(string headerName)
    {
        RemoteHttpResponseHelper.IsRestrictedHeader(headerName).ShouldBeFalse();
    }
}
