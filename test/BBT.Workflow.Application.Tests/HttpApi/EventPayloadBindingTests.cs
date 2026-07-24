using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Orchestration.Controllers.Instances;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// Guards the content-type-independent body binding of the <c>/instances/events</c> endpoint.
/// Kafka / pub-sub sources routed through Dapr deliver event bodies with no content type or
/// <c>application/octet-stream</c>; the endpoint reads the raw body manually so those are accepted
/// instead of being rejected with 415 by <c>[FromBody]</c> JSON model binding.
/// </summary>
public sealed class EventPayloadBindingTests
{
    private const string PayloadJson = """{"accountNo":"9530","balance":52294471}""";

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData(null)]
    public async Task ReadEventPayload_ParsesJson_RegardlessOfContentType(string? contentType)
    {
        var request = BuildRequest(PayloadJson, contentType);

        var payload = await InstanceController.ReadEventPayloadAsync(request, CancellationToken.None);

        payload.ValueKind.ShouldBe(JsonValueKind.Object);
        payload.GetProperty("accountNo").GetString().ShouldBe("9530");
        payload.GetProperty("balance").GetInt64().ShouldBe(52294471);
    }

    [Fact]
    public async Task ReadEventPayload_EmptyBody_ReturnsUndefined()
    {
        var request = BuildRequest(string.Empty, "application/octet-stream");

        var payload = await InstanceController.ReadEventPayloadAsync(request, CancellationToken.None);

        payload.ValueKind.ShouldBe(JsonValueKind.Undefined);
    }

    [Fact]
    public async Task ReadEventPayload_MalformedJson_ThrowsJsonException()
    {
        var request = BuildRequest("{ not-json", "application/octet-stream");

        await Should.ThrowAsync<JsonException>(() =>
            InstanceController.ReadEventPayloadAsync(request, CancellationToken.None));
    }

    private static HttpRequest BuildRequest(string body, string? contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        if (contentType is not null)
            context.Request.ContentType = contentType;

        return context.Request;
    }
}
