using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions.Tasks;

/// <summary>
/// Regression tests for serializing tasks that leave optional <see cref="JsonElement"/> config fields
/// unset (Undefined). Before the fix, serializing such a task (component/artifact cache, instance-task
/// request audit) threw <see cref="System.InvalidOperationException"/> in the built-in JsonElement
/// converter; <c>SafeJsonElementConverter</c> now emits <c>null</c> instead.
/// </summary>
public sealed class StateStoreTaskSerializationTests
{
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Serialize_SetTaskWithoutQueryOrMetadata_DoesNotThrow_ViaCentralOptions()
    {
        // A 'set' task supplies a value at runtime (mapping) — the static config has no value/query/metadata,
        // so those JsonElement properties stay Undefined.
        var task = StateStoreTask.Create(Json("""{ "command": "set", "key": "flag:cacheable-ff" }"""));

        string json = null!;
        Should.NotThrow(() => json = JsonSerializer.Serialize(task, JsonSerializerConstants.JsonOptions));

        using var doc = JsonDocument.Parse(json);
        // Undefined JsonElement fields are emitted as null rather than throwing.
        doc.RootElement.GetProperty("query").ValueKind.ShouldBe(JsonValueKind.Null);
        doc.RootElement.GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
        doc.RootElement.GetProperty("metadata").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Serialize_GetTask_DoesNotThrow_WithDefaultOptions()
    {
        // Default options carry no global converter — this proves the per-property [JsonConverter]
        // attribute makes serialization options-independent (covers the Aether cache path too).
        var task = StateStoreTask.Create(Json("""{ "command": "get", "key": "flag:cacheable-ff" }"""));

        Should.NotThrow(() => JsonSerializer.Serialize(task, new JsonSerializerOptions()));
    }

    [Fact]
    public void Serialize_SetTaskWithValue_PreservesValue()
    {
        var task = StateStoreTask.Create(Json("""{ "command": "set", "key": "k", "value": { "name": "Ada" } }"""));

        var json = JsonSerializer.Serialize(task, JsonSerializerConstants.JsonOptions);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("value").GetProperty("name").GetString().ShouldBe("Ada");
    }
}
