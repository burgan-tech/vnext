using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions.Functions;

public sealed class FunctionCacheTests
{
    [Fact]
    public void Deserialize_ParsesKeyExpressionTtlAndBypass()
    {
        var json = """
        {
          "keyExpression": { "location": "dynamicExpresso", "code": "\"dcs:\" + context.Headers.configKey", "encoding": "NAT" },
          "storeName": "vnext-state",
          "ttlInSeconds": 300,
          "consistency": "Eventual",
          "bypassOnCacheError": false
        }
        """;

        var cache = JsonSerializer.Deserialize<FunctionCache>(json, JsonSerializerConstants.JsonOptions);

        cache.ShouldNotBeNull();
        cache!.KeyExpression.ShouldNotBeNull();
        cache.KeyExpression!.Location.ShouldBe("dynamicExpresso");
        cache.KeyExpression.HasMappingCode.ShouldBeTrue();
        cache.StoreName.ShouldBe("vnext-state");
        cache.TtlInSeconds.ShouldBe(300);
        cache.Consistency.ShouldBe("Eventual");
        cache.BypassOnCacheError.ShouldBeFalse();
        cache.HasKeySource.ShouldBeTrue();
    }

    [Fact]
    public void Deserialize_StaticKey_HasKeySource_AndBypassDefaultsTrue()
    {
        var cache = JsonSerializer.Deserialize<FunctionCache>(
            """{ "key": "dcs:static" }""", JsonSerializerConstants.JsonOptions);

        cache.ShouldNotBeNull();
        cache!.Key.ShouldBe("dcs:static");
        cache.KeyExpression.ShouldBeNull();
        cache.HasKeySource.ShouldBeTrue();
        cache.BypassOnCacheError.ShouldBeTrue();
    }

    [Fact]
    public void HasKeySource_False_WhenNeitherKeyNorExpression()
    {
        var cache = JsonSerializer.Deserialize<FunctionCache>(
            """{ "ttlInSeconds": 60 }""", JsonSerializerConstants.JsonOptions);

        cache.ShouldNotBeNull();
        cache!.HasKeySource.ShouldBeFalse();
    }
}
