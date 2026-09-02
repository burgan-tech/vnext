using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution.Configuration;
using BBT.Workflow.Execution.Python.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

public sealed class DockerContainerExecutionDriverTests
{
    [Fact]
    public async Task ExecuteAsync_UsesHardenedContainerSettingsAndAlwaysRemovesContainer()
    {
        JsonElement? createPayload = null;
        var stopped = false;
        var removed = false;
        var handler = new StubHandler(async (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (request.Method == HttpMethod.Get && path.Contains("/images/"))
            {
                return Response(HttpStatusCode.OK, "{}");
            }

            if (request.Method == HttpMethod.Post && path.Contains("/containers/create"))
            {
                createPayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
                return Response(HttpStatusCode.Created, "{\"id\":\"container-1\"}");
            }

            if (request.Method == HttpMethod.Put && path.Contains("/archive"))
            {
                return Response(HttpStatusCode.OK, "");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/start"))
            {
                return Response(HttpStatusCode.NoContent, "");
            }

            if (request.Method == HttpMethod.Post && path.Contains("/wait"))
            {
                return Response(HttpStatusCode.OK, "{\"statusCode\":0}");
            }

            if (request.Method == HttpMethod.Get && path.Contains("/logs"))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Frame(1, "{\"success\":true}"))
                };
                return response;
            }

            if (request.Method == HttpMethod.Post && path.Contains("/stop"))
            {
                stopped = true;
                return Response(HttpStatusCode.NotModified, "");
            }

            if (request.Method == HttpMethod.Delete)
            {
                removed = true;
                return Response(HttpStatusCode.NoContent, "");
            }

            throw new InvalidOperationException($"Unexpected Docker request: {request.Method} {path}");
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://docker") };
        var driver = new DockerContainerExecutionDriver(
            new PythonContainerOptions { ApiVersion = "v1.43", PullPolicy = "never" },
            client,
            NullLogger<DockerContainerExecutionDriver>.Instance);

        var result = await driver.ExecuteAsync(new ContainerExecutionRequest(
            "runner:test",
            "{}",
            TimeSpan.FromSeconds(5),
            2L * 1024 * 1024 * 1024,
            1_000_000_000,
            128,
            64L * 1024 * 1024,
            "none",
            1024,
            new Dictionary<string, string> { ["PIP_NO_INDEX"] = "1" }));

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldBe("{\"success\":true}");
        stopped.ShouldBeTrue();
        removed.ShouldBeTrue();
        createPayload.ShouldNotBeNull();
        createPayload!.Value.GetProperty("user").GetString().ShouldBe("65532:65532");
        createPayload.Value.GetProperty("networkDisabled").GetBoolean().ShouldBeTrue();
        var hostConfig = createPayload.Value.GetProperty("hostConfig");
        hostConfig.GetProperty("readonlyRootfs").GetBoolean().ShouldBeTrue();
        hostConfig.GetProperty("networkMode").GetString().ShouldBe("none");
        hostConfig.GetProperty("memory").GetInt64().ShouldBe(2L * 1024 * 1024 * 1024);
        hostConfig.GetProperty("nanoCpus").GetInt64().ShouldBe(1_000_000_000);
        hostConfig.GetProperty("pidsLimit").GetInt64().ShouldBe(128);
        hostConfig.GetProperty("capDrop")[0].GetString().ShouldBe("ALL");
        hostConfig.GetProperty("securityOpt")[0].GetString().ShouldBe("no-new-privileges:true");
        hostConfig.TryGetProperty("binds", out _).ShouldBeFalse();
        hostConfig.TryGetProperty("mounts", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutKillsAndRemovesContainer()
    {
        var killed = false;
        var stopped = false;
        var removed = false;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (request.Method == HttpMethod.Get && path.Contains("/images/"))
            {
                return Response(HttpStatusCode.OK, "{}");
            }

            if (request.Method == HttpMethod.Post && path.Contains("/containers/create"))
            {
                return Response(HttpStatusCode.Created, "{\"id\":\"container-timeout\"}");
            }

            if ((request.Method == HttpMethod.Put && path.Contains("/archive")) ||
                (request.Method == HttpMethod.Post && path.EndsWith("/start")))
            {
                return Response(HttpStatusCode.NoContent, "");
            }

            if (request.Method == HttpMethod.Post && path.Contains("/wait"))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (request.Method == HttpMethod.Post && path.Contains("/kill"))
            {
                killed = true;
                return Response(HttpStatusCode.NoContent, "");
            }

            if (request.Method == HttpMethod.Post && path.Contains("/stop"))
            {
                stopped = true;
                return Response(HttpStatusCode.NotModified, "");
            }

            if (request.Method == HttpMethod.Delete)
            {
                removed = true;
                return Response(HttpStatusCode.NoContent, "");
            }

            throw new InvalidOperationException($"Unexpected Docker request: {request.Method} {path}");
        });
        var driver = new DockerContainerExecutionDriver(
            new PythonContainerOptions { ApiVersion = "v1.43", PullPolicy = "never" },
            new HttpClient(handler) { BaseAddress = new Uri("http://docker") },
            NullLogger<DockerContainerExecutionDriver>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() => driver.ExecuteAsync(
            new ContainerExecutionRequest(
                "runner:test",
                "{}",
                TimeSpan.FromMilliseconds(100),
                1024 * 1024,
                1_000_000,
                8,
                1024 * 1024,
                "none",
                1024,
                new Dictionary<string, string>())));

        killed.ShouldBeTrue();
        stopped.ShouldBeTrue();
        removed.ShouldBeTrue();
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static byte[] Frame(byte stream, string content)
    {
        var payload = Encoding.UTF8.GetBytes(content);
        var frame = new byte[8 + payload.Length];
        frame[0] = stream;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
