using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Discovery;
using BBT.Workflow.Gateway;
using BBT.Workflow.Remote.Configuration;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Gateway;

/// <summary>
/// Covers <see cref="RemoteRelatedInstanceReader"/>'s HTTP status mapping — 204 vs 404 vs 200 — and the
/// all-or-nothing batching contract. Follows the inline stub-<see cref="HttpMessageHandler"/> pattern
/// established by <c>RemoteInstanceQueryStateCallHeaderTests.CapturingHandler</c>; there is no shared
/// mocking library for <see cref="HttpClient"/> in this codebase.
/// </summary>
public sealed class RemoteRelatedInstanceReaderTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly RelatedInstanceRef Reference = new(InstanceId, "lending", "loan-application", "2.1.0");

    private static (RemoteRelatedInstanceReader Reader, IDomainDiscoveryResolver Resolver, RoutingHandler Handler) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new RoutingHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var resolver = Substitute.For<IDomainDiscoveryResolver>();
        resolver
            .GetEndpointAsync(Arg.Any<string>(), Arg.Any<EndpointKind>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Result<DiscoveryEndpoint>.Ok(
                new DiscoveryEndpoint(EndpointKind.Url, new Uri($"https://{callInfo.Arg<string>()}.test/"))));

        var reader = new RemoteRelatedInstanceReader(
            new HttpClient(handler), Options.Create(new RemoteOptions()), resolver);

        return (reader, resolver, handler);
    }

    private static RelatedInstanceSnapshot Snapshot(Guid id, string domain = "lending", string flow = "loan-application") => new()
    {
        InstanceId = id,
        Key = "customer-42",
        Domain = domain,
        Flow = flow,
        FlowVersion = "9.9.9", // deliberately different from the reference — must be overridden by Normalize
        Status = "A",
        CurrentState = "awaiting-kyc",
        IsCompleted = false,
        Data = new Dictionary<string, object?> { ["amount"] = 100 }
    };

    [Fact]
    public async Task ReadAsync_NoContent_ReturnsOkNull()
    {
        var (reader, _, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await reader.ReadAsync(Reference, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task ReadAsync_NotFound_ReturnsFail_NotAbsence()
    {
        // A 404 means the route or target app id is wrong — an infrastructure fault — and must never be
        // mistaken for "the instance does not exist" (which is what 204 means).
        var (reader, _, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found")
        });

        var result = await reader.ReadAsync(Reference, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadAsync_Ok_ReturnsSnapshot_WithReferenceAsAuthoritativeForDomainFlowVersion()
    {
        var wireSnapshot = Snapshot(InstanceId);
        var (reader, _, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(wireSnapshot, JsonSerializerConstants.JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json")
        });

        var result = await reader.ReadAsync(Reference, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.InstanceId.ShouldBe(InstanceId);
        result.Value.Domain.ShouldBe(Reference.Domain);
        result.Value.Flow.ShouldBe(Reference.Flow);
        result.Value.FlowVersion.ShouldBe(Reference.FlowVersion);
    }

    [Fact]
    public async Task ReadAsync_Ok_ConvertsDataToExpandoObject_NotJsonElement()
    {
        // Headline behavior under test: RelatedInstanceSnapshot.Data is `dynamic?`, which System.Text.Json
        // deserializes into a JsonElement (there is no converter for a plain `object`-declared target).
        // The local reader's Data always comes out as an ExpandoObject, so a script reading
        // `context.Related.ParentAsync().Data.amount` must see the same shape regardless of whether the
        // parent lives in this domain or another one.
        var wireSnapshot = Snapshot(InstanceId);
        var (reader, _, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(wireSnapshot, JsonSerializerConstants.JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json")
        });

        var result = await reader.ReadAsync(Reference, CancellationToken.None);

        object? data = result.Value!.Data;
        data.ShouldBeOfType<ExpandoObject>();
        ((IDictionary<string, object?>)data!)["amount"].ShouldBe(100);
    }

    [Fact]
    public async Task ReadAsync_ResolverFailure_ReturnsFail_WithoutHttpCall()
    {
        var handler = new RoutingHandler(_ => throw new InvalidOperationException("must not be called"));
        var resolver = Substitute.For<IDomainDiscoveryResolver>();
        resolver
            .GetEndpointAsync(Arg.Any<string>(), Arg.Any<EndpointKind>(), Arg.Any<CancellationToken>())
            .Returns(Result<DiscoveryEndpoint>.Fail(Error.NotFound("domain_not_found", "unknown domain")));

        var reader = new RemoteRelatedInstanceReader(
            new HttpClient(handler), Options.Create(new RemoteOptions()), resolver);

        var result = await reader.ReadAsync(Reference, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadManyAsync_EmptyInput_ReturnsOkEmpty_WithoutHttpCall()
    {
        var (reader, _, handler) = CreateSut(_ => throw new InvalidOperationException("must not be called"));

        var result = await reader.ReadManyAsync([], CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadManyAsync_TwoDomains_SendsTwoBatchPosts()
    {
        var refA = new RelatedInstanceRef(Guid.NewGuid(), "lending", "loan-application", "1.0.0");
        var refB = new RelatedInstanceRef(Guid.NewGuid(), "compliance", "kyc-flow", "1.0.0");

        var (reader, _, handler) = CreateSut(request =>
        {
            var domain = request.RequestUri!.Host.Split('.')[0];
            var snapshot = Snapshot(
                domain == "lending" ? refA.InstanceId : refB.InstanceId,
                domain,
                domain == "lending" ? "loan-application" : "kyc-flow");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new[] { snapshot }, JsonSerializerConstants.JsonOptions),
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });

        var result = await reader.ReadManyAsync([refA, refB], CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Count.ShouldBe(2);
        handler.Requests.Count.ShouldBe(2);
        handler.Requests.ShouldAllBe(r => r.Method == HttpMethod.Post);
        handler.Requests.Select(r => r.RequestUri!.Host).ShouldBe(["lending.test", "compliance.test"], ignoreOrder: true);
    }

    [Fact]
    public async Task ReadManyAsync_OneGroupFails_WholeCallFails_NoPartialResults()
    {
        var refA = new RelatedInstanceRef(Guid.NewGuid(), "lending", "loan-application", "1.0.0");
        var refB = new RelatedInstanceRef(Guid.NewGuid(), "compliance", "kyc-flow", "1.0.0");

        var (reader, _, _) = CreateSut(request =>
        {
            var domain = request.RequestUri!.Host.Split('.')[0];
            if (domain == "compliance")
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("down")
                };

            var snapshot = Snapshot(refA.InstanceId, "lending", "loan-application");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new[] { snapshot }, JsonSerializerConstants.JsonOptions),
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });

        var result = await reader.ReadManyAsync([refA, refB], CancellationToken.None);

        // Must fail outright — never a partial list of just refA's snapshot. A partial result would let a
        // script see "one of two children" and treat the missing one as absent rather than as a fault.
        result.IsSuccess.ShouldBeFalse();
    }

    /// <summary>
    /// Records every request it sees and dispatches to a caller-supplied responder. Mirrors
    /// <c>RemoteInstanceQueryStateCallHeaderTests.CapturingHandler</c>, extended to record all requests
    /// (plural) so multi-group batch tests can assert call count and targets.
    /// </summary>
    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
