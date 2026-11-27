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
    public void Configure_KeyAsString_IsStaticKey()
    {
        var task = CacheAsideTask.Create(Json("""
        { "key": "customer:42", "sourceTask": { "key": "src", "domain": "core", "version": "1.0.0" } }
        """));

        task.CacheKey.ShouldBe("customer:42");
        task.KeyScript.ShouldBeNull();
    }

    [Fact]
    public void Configure_KeyAsObject_IsKeyScript()
    {
        var task = CacheAsideTask.Create(Json("""
        {
          "key": { "location": "dynamicExpresso", "code": "\"customer:\" + context.Instance.Key", "encoding": "NAT" },
          "sourceTask": { "key": "src", "domain": "core", "version": "1.0.0" }
        }
        """));

        task.CacheKey.ShouldBe(string.Empty);
        task.KeyScript.ShouldNotBeNull();
        task.KeyScript!.Location.ShouldBe("dynamicExpresso");
        task.KeyScript.HasMappingCode.ShouldBeTrue();
    }

    [Fact]
    public void Configure_KeyAsReferenceObject_KeepsTheReference()
    {
        var task = CacheAsideTask.Create(Json("""
        {
          "key": { "encoding": "REF", "code": { "key": "k", "domain": "core", "flow": "sys-mappings", "version": "1.0.0" } },
          "sourceTask": { "key": "src", "domain": "core", "version": "1.0.0" }
        }
        """));

        task.KeyScript!.IsReference.ShouldBeTrue();
        task.KeyScript.CodeReference!.Key.ShouldBe("k");
    }

    [Fact]
    public void CloneTyped_CopiesAllFields()
    {
        var config = Json("""
        {
          "key": { "location": "dynamicExpresso", "code": "\"k1\"", "encoding": "NAT" },
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
        clone.CacheKey.ShouldBe(string.Empty);
        clone.KeyScript.ShouldNotBeNull();
        clone.KeyScript!.Location.ShouldBe("dynamicExpresso");
        clone.StoreName.ShouldBe("store");
        clone.TtlInSeconds.ShouldBe(60);
        clone.Consistency.ShouldBe("Strong");
        clone.SourceTask.Key.ShouldBe("src");
        clone.ForceRefresh.ShouldBeTrue();

        var pooled = CacheAsideTask.CreateEmpty();
        pooled.CopyFromInternal(original);
        pooled.KeyScript.ShouldNotBeNull();
        pooled.KeyScript!.Location.ShouldBe("dynamicExpresso");
        pooled.Reset();
        pooled.KeyScript.ShouldBeNull();
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

    private const string BlocksJson = """
    {
      "key": "k1",
      "sourceTask": { "key": "src", "domain": "core", "version": "1.0.0" },
      "sourceInput": { "set": { "$.url": "\"https://x/\" + data.id", "$.headers.x": "\"1\"" } },
      "sourceOutput": { "select": "$.body.profile", "into": "$.profile", "set": { "$.profile.at": "now" } }
    }
    """;

    [Fact]
    public void Configure_ParsesSourceBlocks_AndCopiesThemOnCloneAndPooling()
    {
        var original = CacheAsideTask.Create(Json(BlocksJson));
        original.SetReference(new Reference("cache-task", "core", "sys-tasks", "1.0.0"));

        original.SourceInput.ShouldNotBeNull();
        original.SourceInput!.Set!.Keys.ShouldBe(["$.url", "$.headers.x"]);
        original.SourceOutput.ShouldNotBeNull();
        original.SourceOutput!.Select.ShouldBe("$.body.profile");
        original.SourceOutput.Into.ShouldBe("$.profile");
        original.SourceMapping.ShouldBeNull();

        var clone = original.CloneTyped();
        clone.SourceInput.ShouldBe(original.SourceInput);
        clone.SourceOutput.ShouldBe(original.SourceOutput);

        var pooled = CacheAsideTask.CreateEmpty();
        pooled.CopyFromInternal(original);
        pooled.SourceInput.ShouldBe(original.SourceInput);
        pooled.SourceOutput.ShouldBe(original.SourceOutput);
        pooled.Reset();
        pooled.SourceInput.ShouldBeNull();
        pooled.SourceOutput.ShouldBeNull();
    }

    [Fact]
    public void SourceBlocks_RoundTripThroughTheDefinitionSerializer()
    {
        var json = $$"""{ "type": "18", "config": {{BlocksJson}} }""";
        var task = (CacheAsideTask)JsonSerializer.Deserialize<WorkflowTask>(json, JsonSerializerConstants.JsonOptions)!;

        var written = JsonSerializer.Serialize<WorkflowTask>(task, JsonSerializerConstants.JsonOptions);
        var back = JsonSerializer.Deserialize<WorkflowTask>(written, JsonSerializerConstants.JsonOptions);

        var roundTripped = back.ShouldBeOfType<CacheAsideTask>();
        roundTripped.SourceInput!.Set!.Keys.ShouldBe(["$.url", "$.headers.x"]);
        roundTripped.SourceInput.Set["$.url"].ShouldBe("\"https://x/\" + data.id");
        roundTripped.SourceOutput!.Select.ShouldBe("$.body.profile");
        roundTripped.SourceOutput.Set!["$.profile.at"].ShouldBe("now");
    }
}
