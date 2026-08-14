using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Discovery;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Remote.Configuration;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

using BBT.Aether.Tracing;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

public sealed class RemoteInstanceCommandAppServiceChildCancelTests
{
    [Fact]
    public async Task CancelChildAsync_PostsTypedTerminationToInternalEndpoint()
    {
        var handler = new RecordingHandler();
        var sut = CreateSut(handler);
        var instanceId = Guid.NewGuid();
        var initiator = Guid.NewGuid();
        var cascade = Guid.NewGuid();
        var input = new ChildSubflowCancelInput(
            "1.2.3",
            new TerminationContext(TerminationOrigin.ParentCascade, initiator, cascade));

        var result = await sut.CancelChildAsync(
            instanceId,
            "child-domain",
            "child-flow",
            input,
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        handler.Request.ShouldNotBeNull();
        handler.Request.Method.ShouldBe(HttpMethod.Post);
        handler.Request.RequestUri!.AbsolutePath.ShouldBe(
            $"/api/v1.0/child-domain/workflows/child-flow/instances/{instanceId}/child-cancel");
        handler.Request.RequestUri.Query.ShouldBeEmpty();
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("version").GetString().ShouldBe("1.2.3");
        var termination = body.RootElement.GetProperty("termination");
        termination.GetProperty("origin").GetString().ShouldBe("parentCascade");
        termination.GetProperty("initiatorInstanceId").GetGuid().ShouldBe(initiator);
        termination.GetProperty("cascadeId").GetGuid().ShouldBe(cascade);
    }

    private static RemoteInstanceCommandAppService CreateSut(RecordingHandler handler)
    {
        var resolver = Substitute.For<IDomainDiscoveryResolver>();
        resolver.GetEndpointAsync("child-domain", EndpointKind.Url, Arg.Any<CancellationToken>())
            .Returns(Result<DiscoveryEndpoint>.Ok(
                new DiscoveryEndpoint(EndpointKind.Url, new Uri("https://child.example/"))));

        return new RemoteInstanceCommandAppService(
            new HttpClient(handler),
            Options.Create(new RemoteOptions()),
            resolver,
            Substitute.For<ICurrentUser>(),
            Substitute.For<ICorrelationIdProvider>());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
