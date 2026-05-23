using System.Text.Json;

namespace BBT.Workflow.Validation;

public interface IJsonSchemaCustomValidationRule
{
    string Name { get; }

    bool IsValid(JsonElement value, JsonElement? parameters);
}
