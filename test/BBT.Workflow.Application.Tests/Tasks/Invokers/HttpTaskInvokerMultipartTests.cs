using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Invokers;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

/// <summary>
/// A <c>multipart/form-data</c> HTTP task: the file's bytes reach the wire unchanged (a body string is sent
/// as UTF-8, which rewrites every byte above 0x7F — the reason multipart exists), the parts keep their order,
/// names, file names and media types, and the multipart body wins over Body / ContentType.
/// </summary>
public sealed class HttpTaskInvokerMultipartTests
{
    // Every byte value, so a re-encoding anywhere on the path shows up as a mismatch.
    private static readonly byte[] AllBytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    [Fact]
    public async Task InvokeAsync_FilePart_ReachesTheWireByteForByte()
    {
        var handler = new CapturingHandler();

        await CreateInvoker(handler).InvokeAsync(Descriptor(new HttpTaskBinding
        {
            Url = "https://checks.local/api/Upload?pairs=1&vlm=1",
            Method = "POST",
            Multipart = [new HttpMultipartPartBinding
            {
                Name = "file", Content = AllBytes, FileName = "cek.pdf", ContentType = "application/pdf"
            }]
        }));

        handler.MediaType.ShouldBe("multipart/form-data");
        handler.Boundary.ShouldNotBeNullOrWhiteSpace();
        handler.Parts.Count.ShouldBe(1);
        var part = handler.Parts[0];
        part.Name.ShouldBe("file");
        part.FileName.ShouldBe("cek.pdf");
        part.ContentType.ShouldBe("application/pdf");
        part.Bytes.ShouldBe(AllBytes);
        // The serialized request itself carries the bytes untouched, not only the in-memory part.
        ContainsSequence(handler.WireBytes, AllBytes).ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeAsync_FieldAndFileParts_KeepTheirOrder_AndMultipartWinsOverBodyAndContentType()
    {
        var handler = new CapturingHandler();

        await CreateInvoker(handler).InvokeAsync(Descriptor(new HttpTaskBinding
        {
            Url = "https://checks.local/api/batch",
            Method = "POST",
            Headers = """{"Content-Type":"application/json","X-Api-Key":"k"}""",
            ContentType = "application/json",
            Body = """{"ignored":true}""",
            Multipart =
            [
                new HttpMultipartPartBinding { Name = "note", Value = "çek görüntüleri" },
                new HttpMultipartPartBinding { Name = "files", Content = [1, 2, 3], FileName = "a.png", ContentType = "image/png" },
                new HttpMultipartPartBinding { Name = "files", Content = [4, 5], FileName = "b.bin" }
            ]
        }));

        handler.MediaType.ShouldBe("multipart/form-data");
        handler.Parts.Select(p => p.Name).ShouldBe(["note", "files", "files"]);
        Encoding.UTF8.GetString(handler.Parts[0].Bytes).ShouldBe("çek görüntüleri");
        handler.Parts[0].FileName.ShouldBeNull();
        handler.Parts[1].Bytes.ShouldBe(new byte[] { 1, 2, 3 });
        handler.Parts[2].ContentType.ShouldBe("application/octet-stream");
        Encoding.UTF8.GetString(handler.WireBytes).ShouldNotContain("ignored");
        handler.RequestHeader("X-Api-Key").ShouldBe("k");
    }

    [Fact]
    public async Task InvokeAsync_GetWithParts_SendsNoBody()
    {
        var handler = new CapturingHandler();

        await CreateInvoker(handler).InvokeAsync(Descriptor(new HttpTaskBinding
        {
            Url = "https://checks.local/api/job/1",
            Method = "GET",
            Multipart = [new HttpMultipartPartBinding { Name = "a", Value = "1" }]
        }));

        handler.HadContent.ShouldBeFalse();
    }

    [Fact]
    public void Mapper_TaskWithParts_CarriesThePartsAndNoBody()
    {
        var task = HttpTask.Create("""{ "url": "https://checks.local/api/Upload", "method": "POST", "body": { "x": 1 } }""".ToJsonElement());
        task.SetReference(new Reference("checksolver-upload", "test", "sys-tasks", "1.0.0"));
        task.AddMultipartFile("file", AllBytes, "cek.pdf", "application/pdf");

        var binding = (HttpTaskBinding)TaskBindingMapper.CreateBinding(task).Value.Binding;

        binding.Body.ShouldBeNull();
        binding.Multipart.ShouldNotBeNull();
        binding.Multipart!.Single().ShouldBe(new HttpMultipartPartBinding
        {
            Name = "file", Content = binding.Multipart![0].Content, FileName = "cek.pdf", ContentType = "application/pdf"
        });
        binding.Multipart[0].Content.ShouldBe(AllBytes);
    }

    [Fact]
    public void Mapper_TaskWithoutParts_KeepsTheBodyAndNoMultipart()
    {
        var task = HttpTask.Create("""{ "url": "https://api.local/x", "method": "POST", "body": { "x": 1 } }""".ToJsonElement());
        task.SetReference(new Reference("plain", "test", "sys-tasks", "1.0.0"));

        var binding = (HttpTaskBinding)TaskBindingMapper.CreateBinding(task).Value.Binding;

        binding.Body.ShouldBe("""{ "x": 1 }"""); // the definition's raw JSON text, as before
        binding.Multipart.ShouldBeNull();
    }

    [Fact]
    public void Binding_JsonRoundTrip_KeepsTheBytes()
    {
        // The Remote path serializes the binding into the invoke envelope and the Execution host reads it back.
        var binding = new HttpTaskBinding
        {
            Url = "https://checks.local/api/Upload",
            Method = "POST",
            Multipart = [new HttpMultipartPartBinding { Name = "file", Content = AllBytes, FileName = "cek.pdf", ContentType = "application/pdf" }]
        };

        var element = JsonSerializer.SerializeToElement(binding);
        var back = element.Deserialize<HttpTaskBinding>()!;

        back.Multipart!.Single().Content.ShouldBe(AllBytes);
        back.Multipart![0].FileName.ShouldBe("cek.pdf");
    }

    private static HttpTaskInvoker CreateInvoker(CapturingHandler handler) =>
        new(new FakeHttpClientFactory(handler), NullLogger<HttpTaskInvoker>.Instance);

    private static TaskDescriptor<HttpTaskBinding> Descriptor(HttpTaskBinding binding) =>
        new() { TaskType = TaskTypes.Http, TaskKey = "checksolver-upload", Binding = binding };

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        }
        return false;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed record CapturedPart(string? Name, string? FileName, string? ContentType, byte[] Bytes);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private System.Net.Http.Headers.HttpRequestHeaders? _headers;

        public bool HadContent { get; private set; }
        public string? MediaType { get; private set; }
        public string? Boundary { get; private set; }
        public byte[] WireBytes { get; private set; } = [];
        public List<CapturedPart> Parts { get; } = [];

        public string? RequestHeader(string name) =>
            _headers?.NonValidated.TryGetValues(name, out var values) == true ? values.ToString() : null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _headers = request.Headers;
            if (request.Content is { } content)
            {
                HadContent = true;
                MediaType = content.Headers.ContentType?.MediaType;
                Boundary = content.Headers.ContentType?.Parameters.FirstOrDefault(p => p.Name == "boundary")?.Value;
                WireBytes = await content.ReadAsByteArrayAsync(cancellationToken);
                if (content is MultipartFormDataContent form)
                {
                    foreach (var part in form)
                    {
                        var disposition = part.Headers.ContentDisposition;
                        Parts.Add(new CapturedPart(
                            disposition?.Name?.Trim('"'),
                            disposition?.FileName?.Trim('"'),
                            part.Headers.ContentType?.MediaType,
                            await part.ReadAsByteArrayAsync(cancellationToken)));
                    }
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
