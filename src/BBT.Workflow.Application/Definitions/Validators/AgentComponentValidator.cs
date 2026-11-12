using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using BBT.Workflow.Agents;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Rules;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates agent components (<c>sys-agents</c>, issue #1105) at publish: a provider profile name,
/// instructions, an object output schema, a positive policy within the environment ceilings, and
/// well-formed inline tools (unique names, a binding per kind, schema-shaped parameters, decodable scripts).
/// </summary>
/// <remarks>
/// Task references of kind 1 and 3 are not resolved here (validators are synchronous and run before the
/// referenced task may be published); an unresolvable task fails the run that calls it.
/// </remarks>
public sealed partial class AgentComponentValidator(IOptions<AiAgentOptions>? options = null) : IComponentValidator
{
    /// <inheritdoc />
    public bool CanHandle(string componentType) => componentType == RuntimeSysSchemaInfo.Agents;

    /// <inheritdoc />
    public ComponentValidationResult Validate(JsonElement attributes)
    {
        var result = new ComponentValidationResult();
        Agent? agent = null;
        try
        {
            agent = attributes.Deserialize<Agent>(JsonSerializerConstants.JsonOptions);
        }
        catch (JsonException ex)
        {
            result.AddError($"Invalid JSON format for agent: {ex.Message}", nameof(Agent));
            return result;
        }

        if (agent is null)
        {
            result.AddError("Failed to deserialize agent from attributes.", nameof(Agent));
            return result;
        }

        Validate(agent, result.ValidationErrors, options?.Value);
        return result;
    }

    /// <summary>Validates an agent definition; shared with the dry-run endpoint.</summary>
    public static void Validate(Agent agent, IList<ValidationResult> errors, AiAgentOptions? ceilings = null)
    {
        const string p = nameof(Agent);

        if (string.IsNullOrWhiteSpace(agent.Provider))
            errors.Add(new ValidationResult("Agent 'provider' (a provider profile name) is required.", [$"{p}.provider"]));

        if (string.IsNullOrWhiteSpace(agent.Instructions?.Resolved))
            errors.Add(new ValidationResult("Agent 'instructions' are required.", [$"{p}.instructions"]));

        if (agent.Runtime != AgentRuntimeKeys.Maf)
            errors.Add(new ValidationResult($"Agent runtime '{agent.Runtime}' is not supported; use '{AgentRuntimeKeys.Maf}'.", [$"{p}.runtime"]));

        if (agent.Output?.Schema is not { } schema || !IsObjectSchema(schema))
            errors.Add(new ValidationResult("Agent 'output.schema' must be a JSON schema with \"type\": \"object\".", [$"{p}.output.schema"]));

        ValidatePolicy(agent.Policy, $"{p}.policy", errors, ceilings);
        ValidateMemory(agent.Memory, $"{p}.memory", errors);

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < agent.Tools.Count; i++)
        {
            ValidateTool(agent.Tools[i], $"{p}.tools[{i}]", names, errors);
        }
    }

    private static void ValidateTool(AgentTool tool, string path, HashSet<string> names, IList<ValidationResult> errors)
    {
        if (!Enum.IsDefined(tool.Kind))
        {
            errors.Add(new ValidationResult($"Tool kind '{(int)tool.Kind}' is not supported (1 task, 2 script, 3 mcp).", [$"{path}.kind"]));
            return;
        }

        if (tool.RequiresApproval)
            errors.Add(new ValidationResult("Tool approval (requiresApproval) is not available yet; remove it or set it to false.", [$"{path}.requiresApproval"]));

        if (tool.Kind == AgentToolKind.Mcp)
        {
            if (tool.Binding.Task is null)
                errors.Add(new ValidationResult("MCP tool requires 'binding.task' (an MCP task, type 24).", [$"{path}.binding.task"]));
            if (tool.Binding.Allow is not { Count: > 0 })
                errors.Add(new ValidationResult("MCP tool requires a non-empty 'binding.allow' list; no server tool is exposed by default.", [$"{path}.binding.allow"]));
            if (tool.Binding.Prefix is { Length: > 0 } prefix && !ToolNamePattern().IsMatch(prefix))
                errors.Add(new ValidationResult("MCP tool 'binding.prefix' may only contain letters, digits, '_' and '-'.", [$"{path}.binding.prefix"]));
            foreach (var allowed in tool.Binding.Allow ?? [])
            {
                var exposed = (tool.Binding.Prefix ?? string.Empty) + allowed;
                if (!ToolNamePattern().IsMatch(exposed))
                    errors.Add(new ValidationResult($"MCP tool name '{exposed}' must match ^[a-zA-Z0-9_-]{{1,64}}$.", [$"{path}.binding.allow"]));
                else if (!names.Add(exposed))
                    errors.Add(new ValidationResult($"Tool name '{exposed}' is used more than once.", [$"{path}.binding.allow"]));
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(tool.Name) || !ToolNamePattern().IsMatch(tool.Name))
            errors.Add(new ValidationResult("Tool 'name' must match ^[a-zA-Z0-9_-]{1,64}$.", [$"{path}.name"]));
        else if (!names.Add(tool.Name))
            errors.Add(new ValidationResult($"Tool name '{tool.Name}' is used more than once.", [$"{path}.name"]));

        if (string.IsNullOrWhiteSpace(tool.Description))
            errors.Add(new ValidationResult("Tool 'description' is required; the model chooses tools by it.", [$"{path}.description"]));

        if (tool.Parameters is not { } parameters || !IsObjectSchema(parameters))
            errors.Add(new ValidationResult("Tool 'parameters' must be a JSON schema with \"type\": \"object\".", [$"{path}.parameters"]));

        if (tool.TimeoutSeconds is <= 0)
            errors.Add(new ValidationResult("Tool 'timeoutSeconds' must be positive.", [$"{path}.timeoutSeconds"]));

        if (tool.ResultMaxChars is <= 0)
            errors.Add(new ValidationResult("Tool 'resultMaxChars' must be positive.", [$"{path}.resultMaxChars"]));

        switch (tool.Kind)
        {
            case AgentToolKind.Task:
                if (tool.Binding.Task is null)
                    errors.Add(new ValidationResult("Task tool requires 'binding.task' (a sys-tasks reference).", [$"{path}.binding.task"]));
                if (tool.Binding.Mapping is not null)
                    ScriptCodeValidator.Validate(tool.Binding.Mapping, $"{path}.binding.mapping", errors);
                break;

            case AgentToolKind.Script:
                if (tool.Binding.Code is not { } code)
                {
                    errors.Add(new ValidationResult("Script tool requires 'binding.code'.", [$"{path}.binding.code"]));
                    break;
                }

                ScriptCodeValidator.Validate(code, $"{path}.binding.code", errors);
                if (ConditionScriptLocations.IsDynamicExpresso(code.Location) &&
                    SafeDecode(code).Length > ConditionScriptLocations.MaxDynamicExpressoExpressionLength)
                {
                    errors.Add(new ValidationResult(
                        $"dynamicExpresso tool expression exceeds {ConditionScriptLocations.MaxDynamicExpressoExpressionLength} characters.",
                        [$"{path}.binding.code"]));
                }

                break;

            default:
                // Mcp is validated above; an undefined kind is rejected at the top.
                break;
        }
    }

    private static void ValidatePolicy(AgentPolicy? policy, string path, IList<ValidationResult> errors, AiAgentOptions? ceilings)
    {
        if (policy is null)
        {
            errors.Add(new ValidationResult("Agent 'policy' is required.", [path]));
            return;
        }

        CheckPositive(policy.MaxIterations, $"{path}.maxIterations", ceilings?.MaxIterations, errors);
        CheckPositive(policy.MaxToolCalls, $"{path}.maxToolCalls", ceilings?.MaxToolCalls, errors, allowZero: true);
        CheckPositive(policy.MaxTokens, $"{path}.maxTokens", ceilings?.MaxTokens, errors);
        CheckPositive(policy.TimeoutSeconds, $"{path}.timeoutSeconds", ceilings?.MaxRunSeconds, errors);
        CheckPositive(policy.ToolTimeoutSeconds, $"{path}.toolTimeoutSeconds", null, errors);
        CheckPositive(policy.ResultMaxChars, $"{path}.resultMaxChars", null, errors);
        CheckPositive(policy.MaxEntries, $"{path}.maxEntries", null, errors);
        if (policy.Retry is { MaxAttempts: < 1 or > 5 })
            errors.Add(new ValidationResult("policy.retry.maxAttempts must be 1..5.", [$"{path}.retry.maxAttempts"]));
    }

    private static void CheckPositive(int? value, string member, int? ceiling, IList<ValidationResult> errors, bool allowZero = false)
    {
        if (value is null)
            return;
        if (value < 0 || (!allowZero && value == 0))
            errors.Add(new ValidationResult($"{member} must be positive.", [member]));
        else if (ceiling is { } max && value > max)
            errors.Add(new ValidationResult($"{member} ({value}) exceeds the environment ceiling ({max}).", [member]));
    }

    private static void ValidateMemory(AgentMemory? memory, string path, IList<ValidationResult> errors)
    {
        if (memory is null)
            return;
        if (memory.Mode == AgentMemoryMode.Key && string.IsNullOrWhiteSpace(memory.Key))
            errors.Add(new ValidationResult("memory.mode 'key' requires memory.key.", [$"{path}.key"]));
        if (memory.Mode != AgentMemoryMode.Key && !string.IsNullOrWhiteSpace(memory.Key))
            errors.Add(new ValidationResult("memory.key is only used with memory.mode 'key'.", [$"{path}.key"]));
        if (memory.MaxMessages is <= 0)
            errors.Add(new ValidationResult("memory.maxMessages must be positive.", [$"{path}.maxMessages"]));
        if (memory.RetentionDays is <= 0)
            errors.Add(new ValidationResult("memory.retentionDays must be positive.", [$"{path}.retentionDays"]));
    }

    private static bool IsObjectSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object &&
        schema.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        type.GetString() == "object";

    private static string SafeDecode(ScriptCode code)
    {
        try
        {
            return code.DecodedCode;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();
}
