using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions.Tasks;

public sealed class CacheAsideTaskTests
{
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Configure_ParsesAllFields()
    {
        var config = Json("""
        {
          "key": "customer:{context.Headers.customerId}:profile",
          "storeName": "customer-cache-store",
          "ttlInSeconds": 300,
          "consistency": "Eventual",
          "sourceTask": { "key": "get-customer-http", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" },
          "sourceMapping": { "location": "./src/mappings/get-customer-cached.csx", "code": "cmV0dXJuIG51bGw7", "encoding": "base64" },
          "bypassOnCacheError": true,
          "forceRefresh": false
        }
        """);

        var task = CacheAsideTask.Create(config);

        task.GetTaskType().ShouldBe(TaskType.CacheAside);
        task.CacheKey.ShouldBe("customer:{context.Headers.customerId}:profile");
        task.StoreName.ShouldBe("customer-cache-store");
        task.TtlInSeconds.ShouldBe(300);
        task.Consistency.ShouldBe("Eventual");
        task.SourceTask.ShouldNotBeNull();
        task.SourceTask.Key.ShouldBe("get-customer-http");
        task.SourceTask.Domain.ShouldBe("core");
        task.SourceTask.Flow.ShouldBe("sys-tasks");
        task.SourceTask.Version.ShouldBe("1.0.0");
        task.SourceMapping.ShouldNotBeNull();
        task.SourceMapping!.HasMappingCode.ShouldBeTrue();
        task.BypassOnCacheError.ShouldBeTrue();
        task.ForceRefresh.ShouldBeFalse();
    }

    [Fact]
    public void Configure_AppliesDefaults_WhenOptionalFieldsAbsent()
    {
        var config = Json("""
        {
          "key": "k1",
          "sourceTask": { "key": "src", "domain": "core", "version": "1.0.0" }
        }
        """);

        var task = CacheAsideTask.Create(config);

        task.CacheKey.ShouldBe("k1");
        task.StoreName.ShouldBe(string.Empty);
        task.TtlInSeconds.ShouldBeNull();
        task.Consistency.ShouldBeNull();
        task.SourceMapping.ShouldBeNull();
        // sourceTask.flow defaults to the runtime tasks schema when omitted.
        task.SourceTask.Flow.ShouldNotBeNullOrWhiteSpace();
        // Defaults per spec.
        task.BypassOnCacheError.ShouldBeTrue();
        task.ForceRefresh.ShouldBeFalse();
    }

    [Fact]
    public void Configure_ForceRefreshTrue_IsParsed()
    {
        var config = Json("""
        {
          "key": "k1",
          "sourceTask": { "key": "src", "domain": "core", "version": "1.0.0" },
          "bypassOnCacheError": false,
          "forceRefresh": true
        }
        """);

        var task = CacheAsideTask.Create(config);

        task.BypassOnCacheError.ShouldBeFalse();
        task.ForceRefresh.ShouldBeTrue();
    }

    [Fact]
    public void Configure_ParsesKeyExpression()
    {
        var config = Json("""
        {
          "key": "customer:profile",
          "keyExpression": { "location": "dynamicExpresso", "code": "\"customer:\" + context.Headers.customerId", "encoding": "NAT" },
          "sourceTask": { "key": "src", "domain": "core", "version": "1.0.0" }
        }
        """);

        var task = CacheAsideTask.Create(config);

        task.KeyExpression.ShouldNotBeNull();
        task.KeyExpression!.Location.ShouldBe("dynamicExpresso");
        task.KeyExpression.HasMappingCode.ShouldBeTrue();
    }

    [Fact]
    public void CloneTyped_CopiesAllFields()
    {
        var config = Json("""
        {
          "key": "k1",
          "storeName": "store",
          "ttlInSeconds": 60,
          "consistency": "Strong",
          "sourceTask": { "key": "src", "domain": "core", "version": "1.0.0" },
          "forceRefresh": true
        }
        """);

        var original = CacheAsideTask.Create(config);
        original.SetReference(new Reference("cache-task", "core", "sys-tasks", "1.0.0"));

        var clone = original.CloneTyped();

        clone.Key.ShouldBe("cache-task");
        clone.CacheKey.ShouldBe("k1");
        clone.StoreName.ShouldBe("store");
        clone.TtlInSeconds.ShouldBe(60);
        clone.Consistency.ShouldBe("Strong");
        clone.SourceTask.Key.ShouldBe("src");
        clone.ForceRefresh.ShouldBeTrue();
    }

    [Fact]
    public void Deserialize_ViaPolymorphicBase_ResolvesToCacheAsideTask()
    {
        var json = """
        {
          "type": "18",
          "config": {
            "key": "k1",
            "sourceTask": { "key": "src", "domain": "core", "version": "1.0.0" }
          }
        }
        """;

        var task = JsonSerializer.Deserialize<WorkflowTask>(json, JsonSerializerConstants.JsonOptions);

        task.ShouldBeOfType<CacheAsideTask>();
        ((CacheAsideTask)task!).CacheKey.ShouldBe("k1");
    }
}
