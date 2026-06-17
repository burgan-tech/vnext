using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Mapping;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Mapping;

public sealed class TaskBindingMapperHttpTaskTests
{
    [Fact]
    public void CreateEnvelope_MapsHttpTaskAcceptedStatusCodes()
    {
        var config = """
            {
              "url": "https://workflow.local/functions/send-otp",
              "method": "POST",
              "acceptedStatusCodes": [ "400", "4xx" ],
              "timeoutSeconds": 30
            }
            """;
        var task = HttpTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("send-otp", "test-domain", "sys-tasks", "1.0.0"));

        var result = TaskBindingMapper.CreateEnvelope(task);

        result.IsSuccess.ShouldBeTrue();
        var binding = result.Value!.Binding.Deserialize<HttpTaskBinding>();
        binding.ShouldNotBeNull();
        binding.AcceptedStatusCodes.ShouldNotBeNull();
        binding.AcceptedStatusCodes.ShouldBe(["400", "4xx"]);
    }

    [Fact]
    public void CreateEnvelope_JsonContentType_PreservesObjectBody()
    {
        var config = """
            {
              "url": "https://workflow.local/endpoint",
              "method": "POST",
              "body": { "name": "test", "count": 1 }
            }
            """;
        var task = HttpTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("http-task", "test-domain", "sys-tasks", "1.0.0"));

        var binding = MapToBinding(task);

        binding.ContentType.ShouldBeNull();
        binding.Body.ShouldNotBeNull();
        // Object body round-trips as JSON.
        var parsed = JsonSerializer.Deserialize<JsonElement>(binding.Body!);
        parsed.GetProperty("name").GetString().ShouldBe("test");
        parsed.GetProperty("count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void CreateEnvelope_NonJsonContentType_UnwrapsStringBody()
    {
        var config = """
            {
              "url": "https://workflow.local/token",
              "method": "POST",
              "contentType": "application/x-www-form-urlencoded",
              "body": "grant_type=client_credentials&client_id=abc"
            }
            """;
        var task = HttpTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("token-task", "test-domain", "sys-tasks", "1.0.0"));

        var binding = MapToBinding(task);

        binding.ContentType.ShouldBe("application/x-www-form-urlencoded");
        // Raw, unquoted form body — no surrounding double-quotes.
        binding.Body.ShouldBe("grant_type=client_credentials&client_id=abc");
    }

    [Fact]
    public void CreateEnvelope_NonJsonContentTypeFromHeader_UnwrapsStringBody()
    {
        var config = """
            {
              "url": "https://workflow.local/token",
              "method": "POST",
              "headers": { "Content-Type": "application/x-www-form-urlencoded" },
              "body": "a=1&b=2"
            }
            """;
        var task = HttpTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("token-task", "test-domain", "sys-tasks", "1.0.0"));

        var binding = MapToBinding(task);

        binding.Body.ShouldBe("a=1&b=2");
    }

    [Fact]
    public void CreateEnvelope_RawBody_SentVerbatim()
    {
        // Odd casing + whitespace that JSON re-serialization would normalize/alter.
        const string signed = "{\n  \"Amount\":100,  \"Currency\" : \"TRY\"\n}";
        var config = $$"""
            {
              "url": "https://workflow.local/pay",
              "method": "POST",
              "contentType": "application/json",
              "rawBody": {{JsonSerializer.Serialize(signed)}}
            }
            """;
        var task = HttpTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("pay-task", "test-domain", "sys-tasks", "1.0.0"));

        var binding = MapToBinding(task);

        binding.ContentType.ShouldBe("application/json");
        binding.Body.ShouldBe(signed); // byte-identical to the signed bytes
    }

    [Fact]
    public void CreateEnvelope_RawBody_TakesPrecedenceOverBody()
    {
        var task = HttpTask.CreateEmpty();
        task.SetUrl("https://workflow.local/pay");
        task.SetBody(new { amount = 1 });
        task.SetRawBody("RAW-VERBATIM");
        task.SetReference(new Reference("pay-task", "test-domain", "sys-tasks", "1.0.0"));

        var binding = MapToBinding(task);

        binding.Body.ShouldBe("RAW-VERBATIM");
    }

    private static HttpTaskBinding MapToBinding(HttpTask task)
    {
        var result = TaskBindingMapper.CreateEnvelope(task);
        result.IsSuccess.ShouldBeTrue();
        var binding = result.Value!.Binding.Deserialize<HttpTaskBinding>();
        binding.ShouldNotBeNull();
        return binding!;
    }
}
