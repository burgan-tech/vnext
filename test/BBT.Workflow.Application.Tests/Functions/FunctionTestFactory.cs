using System.Text.Json;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Functions;

/// <summary>
/// Builds <see cref="Function"/> instances from component JSON. The contract slots
/// (<c>inputSchema</c>, <c>outputSchema</c>, <c>inputView</c>, <c>outputView</c>) are polymorphic and
/// bound by converters rather than constructor parameters, so JSON is the only way to construct them
/// the way the runtime actually does.
/// </summary>
internal static class FunctionTestFactory
{
    public const string Domain = "test-domain";
    public const string Version = "1.0.0";

    /// <summary>
    /// Deserializes a function from its <c>attributes</c> JSON and stamps the given key.
    /// </summary>
    public static Function FromJson(string attributesJson, string key = "my-fn")
    {
        var function = JsonSerializer.Deserialize<Function>(attributesJson, JsonSerializerConstants.JsonOptions)!;
        function.SetReference(new Reference(key, Domain, "sys-functions", Version));
        return function;
    }

    /// <summary>
    /// Function JSON with a single mandatory task and whatever extra attribute fragment is supplied.
    /// </summary>
    public static string Attributes(string? extra = null, string scope = "D")
    {
        var task = $$"""
            "task": {
                "order": 1,
                "task": { "key": "my-task", "domain": "{{Domain}}", "flow": "sys-tasks", "version": "{{Version}}" },
                "mapping": { "location": "", "code": "", "encoding": "NAT" }
            }
            """;

        return string.IsNullOrWhiteSpace(extra)
            ? $$"""{ "scope": "{{scope}}", {{task}} }"""
            : $$"""{ "scope": "{{scope}}", {{task}}, {{extra}} }""";
    }

    /// <summary>A component reference literal, for embedding in slot JSON.</summary>
    public static string Ref(string key, string flow) =>
        $$"""{ "key": "{{key}}", "domain": "{{Domain}}", "flow": "{{flow}}", "version": "{{Version}}" }""";

    /// <summary>A rule <c>ScriptCode</c> literal carrying native (unencoded) code.</summary>
    public static string Rule(string code) =>
        $$"""{ "location": "", "code": "{{code}}", "encoding": "NAT" }""";
}
