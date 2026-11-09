using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// MCP (Model Context Protocol) task, type 24 (issue #1105). Calls one operation of a remote MCP server
/// over the streamable HTTP transport from the orchestration host. Used standalone (onEntry, onExit,
/// functions) and as the binding of an agent's <see cref="AgentToolKind.Mcp"/> tool, where the runtime
/// overrides <see cref="Operation"/> with <c>listTools</c> and <c>callTool</c>.
/// </summary>
/// <remarks>
/// The component never carries a credential: <see cref="Auth"/> and every <see cref="Headers"/> value
/// name a secret (<c>secretRef</c>) resolved at call time.
/// </remarks>
public sealed class McpTask : WorkflowTask
{
    /// <summary>Default per-call timeout.</summary>
    public const int DefaultTimeoutSeconds = 20;

    /// <summary>Upper bound of <see cref="TimeoutSeconds"/>.</summary>
    public const int MaxTimeoutSeconds = 120;

    private McpTask()
    {
    }

    [JsonConstructor]
    private McpTask(JsonElement config) : base(config)
    {
        Type = ((int)TaskType.Mcp).ToString();
    }

    /// <summary>Transport; only <c>http</c> is supported.</summary>
    public string Transport { get; private set; } = McpTransports.Http;

    /// <summary>Server endpoint; must match an allowed host.</summary>
    public string Endpoint { get; private set; } = string.Empty;

    /// <summary>Authorization header built from a secret.</summary>
    public McpAuth? Auth { get; private set; }

    /// <summary>Extra headers; each value names a secret.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; private set; } = new Dictionary<string, string>();

    /// <summary>Header values authored as plain text (rejected at publish).</summary>
    public IReadOnlyList<string> PlainTextHeaders { get; private set; } = [];

    /// <summary>Operation to run (<see cref="McpOperations"/>).</summary>
    public string Operation { get; private set; } = McpOperations.CallTool;

    /// <summary><c>callTool</c>: the tool name.</summary>
    public string? Tool { get; private set; }

    /// <summary><c>readResource</c>: the resource uri.</summary>
    public string? Uri { get; private set; }

    /// <summary><c>getPrompt</c>: the prompt name.</summary>
    public string? Prompt { get; private set; }

    /// <summary>Static arguments; an input mapping or the agent replaces them.</summary>
    public JsonElement? Arguments { get; private set; }

    /// <summary>Per-call timeout.</summary>
    public int TimeoutSeconds { get; private set; } = DefaultTimeoutSeconds;

    /// <summary>Sets the operation (agent tool path).</summary>
    public void SetOperation(string operation) => Operation = operation;

    /// <summary>Sets the tool name (agent tool path).</summary>
    public void SetTool(string? tool) => Tool = tool;

    /// <summary>Sets the call arguments (input mapping or agent tool path).</summary>
    public void SetArguments(dynamic? arguments) =>
        Arguments = arguments is null ? null : JsonSerializer.SerializeToElement(arguments, JsonSerializerConstants.JsonOptions);

    /// <summary>Sets the call arguments from a JSON element.</summary>
    public void SetArgumentsElement(JsonElement? arguments) => Arguments = arguments;

    /// <inheritdoc />
    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("transport", out var transport) && transport.ValueKind == JsonValueKind.String)
            Transport = transport.GetString() ?? McpTransports.Http;

        if (config.TryGetProperty("endpoint", out var endpoint) && endpoint.ValueKind == JsonValueKind.String)
            Endpoint = endpoint.GetString() ?? string.Empty;

        if (config.TryGetProperty("auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
        {
            Auth = new McpAuth(
                ReadString(auth, "header") ?? "Authorization",
                ReadString(auth, "scheme"),
                ReadString(auth, "secretRef"));
        }

        if (config.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var plain = new List<string>();
            foreach (var header in headers.EnumerateObject())
            {
                if (header.Value.ValueKind == JsonValueKind.Object &&
                    ReadString(header.Value, "secretRef") is { Length: > 0 } secretRef)
                {
                    map[header.Name] = secretRef;
                }
                else
                {
                    plain.Add(header.Name);
                }
            }

            Headers = map;
            PlainTextHeaders = plain;
        }

        if (config.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.String)
            Operation = operation.GetString() ?? McpOperations.CallTool;

        Tool = config.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String ? tool.GetString() : null;
        Uri = config.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String ? uri.GetString() : null;
        Prompt = config.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String ? prompt.GetString() : null;

        if (config.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object)
            Arguments = arguments.Clone();

        if (config.TryGetProperty("timeoutSeconds", out var timeout) && timeout.ValueKind == JsonValueKind.Number)
            TimeoutSeconds = timeout.GetInt32();
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Creates a task from its config.</summary>
    public static McpTask Create(JsonElement config) => new(config);

    /// <summary>Creates an empty task for pooling.</summary>
    public static McpTask CreateEmpty() => new();

    /// <inheritdoc />
    public override WorkflowTask Clone() => CloneTyped();

    /// <summary>Typed deep copy.</summary>
    public McpTask CloneTyped()
    {
        var cloned = new McpTask();
        CopyBaseTo(cloned);
        cloned.CopyOwn(this);
        return cloned;
    }

    /// <summary>Copies every member for object pooling.</summary>
    public void CopyFromInternal(McpTask source)
    {
        source.CopyBaseToInternal(this);
        CopyOwn(source);
    }

    private void CopyOwn(McpTask source)
    {
        Transport = source.Transport;
        Endpoint = source.Endpoint;
        Auth = source.Auth;
        Headers = source.Headers;
        PlainTextHeaders = source.PlainTextHeaders;
        Operation = source.Operation;
        Tool = source.Tool;
        Uri = source.Uri;
        Prompt = source.Prompt;
        Arguments = source.Arguments;
        TimeoutSeconds = source.TimeoutSeconds;
    }

    /// <inheritdoc />
    public override void Reset()
    {
        base.Reset();
        Transport = McpTransports.Http;
        Endpoint = string.Empty;
        Auth = null;
        Headers = new Dictionary<string, string>();
        PlainTextHeaders = [];
        Operation = McpOperations.CallTool;
        Tool = null;
        Uri = null;
        Prompt = null;
        Arguments = null;
        TimeoutSeconds = DefaultTimeoutSeconds;
    }
}

/// <summary>Authorization of an MCP task: <c>{Header}: {Scheme} {secret}</c>.</summary>
/// <param name="Header">Header name; <c>Authorization</c> by default.</param>
/// <param name="Scheme">Optional scheme prefix such as <c>Bearer</c>.</param>
/// <param name="SecretRef">Secret reference (<c>store/name</c> or a configuration key).</param>
public sealed record McpAuth(string Header, string? Scheme, string? SecretRef);

/// <summary>MCP transports.</summary>
public static class McpTransports
{
    /// <summary>Streamable HTTP.</summary>
    public const string Http = "http";
}

/// <summary>MCP task operations.</summary>
public static class McpOperations
{
    /// <summary>Invoke a tool.</summary>
    public const string CallTool = "callTool";

    /// <summary>List the server's tools.</summary>
    public const string ListTools = "listTools";

    /// <summary>Read a resource.</summary>
    public const string ReadResource = "readResource";

    /// <summary>List resources.</summary>
    public const string ListResources = "listResources";

    /// <summary>Render a prompt.</summary>
    public const string GetPrompt = "getPrompt";

    /// <summary>List prompts.</summary>
    public const string ListPrompts = "listPrompts";

    /// <summary>Every supported operation.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        CallTool, ListTools, ReadResource, ListResources, GetPrompt, ListPrompts
    };
}
