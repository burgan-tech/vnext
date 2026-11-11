using System.Collections.Concurrent;
using System.Dynamic;
using System.Text.Json;
using BBT.Workflow.Validation;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Agents;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Factory;
using BBT.Workflow.Tasks.Executors;
using DynamicExpresso;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Agents.Tools;

/// <summary>What the catalog needs to build the tools of one run.</summary>
/// <param name="Agent">The agent component.</param>
/// <param name="Exclude">Tool names hidden in this state.</param>
/// <param name="CallerRoles">Caller roles (tool <c>roles</c> filter).</param>
/// <param name="ScriptContext">The hop's script context; tools run on parallel branches of it.</param>
/// <param name="TransitionId">Transition record id (task journal rows).</param>
/// <param name="Recorder">Run recorder.</param>
/// <param name="Stub">Dry-run: every tool answers from its schema, nothing is called.</param>
public sealed record AgentToolBuildContext(
    Agent Agent,
    IReadOnlySet<string> Exclude,
    IReadOnlyCollection<string> CallerRoles,
    ScriptContext ScriptContext,
    Guid? TransitionId,
    AgentRunRecorder Recorder,
    bool Stub);

/// <summary>
/// Turns an agent's inline tools into the tool definitions a run offers (issue #1105):
/// kind 1 runs a <c>sys-tasks</c> component through the task engine on its own DI scope and a parallel
/// branch of the script context, with <see cref="TaskEngineExecutionOptions.SuppressDataApply"/> (tools
/// never write instance data; the agent writes once); kind 2 evaluates a dynamicExpresso expression or a
/// C# <see cref="IToolHandler"/>; kind 3 lists an MCP task's tools (memoized) and calls them through the
/// task engine. Every call goes through <see cref="AgentRunRecorder.Wrap"/>.
/// </summary>
public sealed class AgentToolCatalog(
    IServiceScopeFactory scopeFactory,
    ITaskFactory taskFactory,
    IScriptEngine scriptEngine,
    IJsonSchemaValidator schemaValidator,
    ILogger<AgentToolCatalog> logger)
{
    private static readonly ConcurrentDictionary<string, (DateTime ExpiresAt, IReadOnlyList<McpToolInfo> Tools)> McpToolMemo = new();
    private static readonly ConcurrentDictionary<string, Lambda> ExpressionMemo = new(StringComparer.Ordinal);
    private static readonly JsonElement EmptyObjectSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    /// <summary>Builds the tool definitions of a run.</summary>
    public async Task<IReadOnlyList<AgentToolDefinition>> BuildAsync(AgentToolBuildContext ctx, CancellationToken cancellationToken)
    {
        var tools = new List<AgentToolDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tool in ctx.Agent.Tools)
        {
            if (tool.Roles is { Count: > 0 } roles && !roles.Any(r => ctx.CallerRoles.Contains(r, StringComparer.OrdinalIgnoreCase)))
                continue;

            if (tool.Kind == AgentToolKind.Mcp)
            {
                foreach (var definition in await BuildMcpToolsAsync(ctx, tool, cancellationToken))
                {
                    if (ctx.Exclude.Contains(definition.Name))
                        continue;
                    if (!names.Add(definition.Name))
                    {
                        logger.AgentToolNameCollision(ctx.Agent.Key, definition.Name);
                        continue;
                    }

                    tools.Add(definition);
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(tool.Name) || ctx.Exclude.Contains(tool.Name))
                continue;
            if (!names.Add(tool.Name))
            {
                logger.AgentToolNameCollision(ctx.Agent.Key, tool.Name);
                continue;
            }

            var parameters = tool.Parameters ?? EmptyObjectSchema;
            Func<AgentToolInvocation, CancellationToken, Task<AgentToolResult>> body = ctx.Stub
                ? StubBody(parameters)
                : tool.Kind switch
                {
                    AgentToolKind.Task => TaskBody(ctx, tool),
                    _ => ScriptBody(ctx, tool)
                };

            var validate = ArgumentCheck(parameters);
            tools.Add(new AgentToolDefinition(
                tool.Name,
                tool.Description ?? tool.Name,
                parameters,
                ctx.Recorder.Wrap(tool.Name, tool.Kind, BindingRef(tool), parameters, tool.TimeoutSeconds, tool.ResultMaxChars, body, validate),
                tool.RequiresApproval)
            {
                ValidateArguments = validate
            });
        }

        return tools;
    }

    /// <summary>The argument check of one tool: null when the arguments match its parameter schema.</summary>
    private Func<JsonElement, string?> ArgumentCheck(JsonElement parameters) =>
        arguments => AgentSchemaCheck.Describe(schemaValidator, parameters, arguments);

    private static string? BindingRef(AgentTool tool) =>
        tool.Binding.Task is { } t ? $"sys-tasks/{t.Domain}/{t.Key}@{t.Version}"
        : tool.Binding.Code is { IsReference: true, CodeReference: { } r } ? $"sys-mappings/{r.Domain}/{r.Key}@{r.Version}"
        : null;

    private static Func<AgentToolInvocation, CancellationToken, Task<AgentToolResult>> StubBody(JsonElement parameters) =>
        (invocation, _) => Task.FromResult(new AgentToolResult(
            AgentToolCallStatus.Completed,
            JsonSerializer.Serialize(new { stub = true, tool = invocation.Name, arguments = invocation.Arguments })));

    private Func<AgentToolInvocation, CancellationToken, Task<AgentToolResult>> TaskBody(AgentToolBuildContext ctx, AgentTool tool) =>
        async (invocation, cancellationToken) =>
        {
            var reference = tool.Binding.Task!;
            var template = await taskFactory.CreateExecutionTaskAsync(reference, cancellationToken);
            if (!template.IsSuccess)
                return new AgentToolResult(AgentToolCallStatus.Failed, string.Empty, $"task '{reference.Key}' could not be resolved: {template.Error.Message}");

            var task = template.Value!;
            if (task.GetTaskType() is TaskType.FanOut)
                return new AgentToolResult(AgentToolCallStatus.Rejected, string.Empty, "a FanOut task cannot run as an agent tool");

            return await RunTaskAsync(ctx, reference, task.Clone(), tool.Binding.Mapping, invocation, cancellationToken);
        };

    private async Task<AgentToolResult> RunTaskAsync(
        AgentToolBuildContext ctx,
        IReference reference,
        WorkflowTask prepared,
        ScriptCode? mapping,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var branch = ctx.ScriptContext.CreateParallelBranch();
        branch.SetBody(JsonSerializer.Deserialize<ExpandoObject>(invocation.Arguments.GetRawText(), JsonSerializerConstants.JsonOptions));

        // An HTTP tool call carries a key the target can deduplicate on: the same run and call id always
        // produce the same key. A mapping that replaces the whole header set drops it.
        if (prepared is HttpTask http)
            http.AddHeader("Idempotency-Key", InstanceAgentToolCall.CreateIdempotencyKey(ctx.Recorder.RunId, invocation.CallId));

        await using var scope = scopeFactory.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ITaskExecutionEngine>();

        var onExecute = OnExecuteTask.Create(
            order: 0,
            task: reference,
            mapping: mapping ?? ScriptCode.FromNative(string.Empty));

        var result = await engine.ExecuteAsync(
            onExecute,
            ctx.TransitionId,
            TaskTrigger.OnEntry,
            TaskExecutionOrigin.Flow,
            branch,
            new TaskEngineExecutionOptions
            {
                PreparedTask = prepared,
                SuppressDataApply = true,
                JournalTaskKey = $"agent:{ctx.Recorder.RunId:N}:{invocation.Name}:{invocation.CallId}",
                CaptureResponse = true
            },
            cancellationToken);

        if (!result.IsSuccess)
            return new AgentToolResult(AgentToolCallStatus.Failed, string.Empty, result.Error.Message ?? result.Error.Code);

        var execution = result.Value!;
        var content = JsonSerializer.Serialize((object?)execution.Response?.Data, JsonSerializerConstants.JsonOptions);
        return execution.IsSuccess
            ? new AgentToolResult(AgentToolCallStatus.Completed, content)
            : new AgentToolResult(AgentToolCallStatus.Failed, content, execution.TaskError?.NormalizedError.Message ?? execution.Response?.ErrorMessage ?? "tool task failed");
    }

    private Func<AgentToolInvocation, CancellationToken, Task<AgentToolResult>> ScriptBody(AgentToolBuildContext ctx, AgentTool tool) =>
        async (invocation, cancellationToken) =>
        {
            var code = tool.Binding.Code!;
            object? value;
            if (ConditionScriptLocations.IsDynamicExpresso(code.Location))
            {
                value = EvaluateExpression(code.DecodedCode.Trim(), invocation.Arguments, ctx.ScriptContext);
            }
            else
            {
                var handler = await scriptEngine.CompileToInstanceAsync<IToolHandler>(
                    code, flowScripts: ctx.ScriptContext.Workflow?.Scripts, cancellationToken: cancellationToken);
                value = await handler.Handler(invocation.Arguments, ctx.ScriptContext.CreateParallelBranch());
            }

            return new AgentToolResult(AgentToolCallStatus.Completed,
                value is string s ? s : JsonSerializer.Serialize(value, JsonSerializerConstants.JsonOptions));
        };

    private static object? EvaluateExpression(string expression, JsonElement args, ScriptContext context)
    {
        var lambda = ExpressionMemo.GetOrAdd(expression, e =>
            new Interpreter(InterpreterOptions.Default)
                .Parse(e,
                    new Parameter("args", typeof(ExpandoObject)),
                    new Parameter("data", typeof(ExpandoObject)),
                    new Parameter("now", typeof(DateTime))));

        var argsObject = JsonSerializer.Deserialize<ExpandoObject>(args.GetRawText(), JsonSerializerConstants.JsonOptions) ?? new ExpandoObject();
        var dataJson = context.Instance?.LatestData?.Data.JsonElement.GetRawText();
        var dataObject = dataJson is null
            ? new ExpandoObject()
            : JsonSerializer.Deserialize<ExpandoObject>(dataJson, JsonSerializerConstants.JsonOptions) ?? new ExpandoObject();

        return lambda.Invoke(argsObject, dataObject, DateTime.UtcNow);
    }

    private async Task<IReadOnlyList<AgentToolDefinition>> BuildMcpToolsAsync(AgentToolBuildContext ctx, AgentTool tool, CancellationToken cancellationToken)
    {
        var reference = tool.Binding.Task!;
        var allow = new HashSet<string>(tool.Binding.Allow ?? [], StringComparer.Ordinal);
        if (allow.Count == 0)
            return [];

        var template = await taskFactory.CreateExecutionTaskAsync(reference, cancellationToken);
        if (!template.IsSuccess || template.Value is not McpTask mcpTemplate)
        {
            logger.McpCallFailed(McpOperations.ListTools, reference.Key, template.IsSuccess ? "binding.task is not an MCP task (type 24)" : template.Error.Message ?? "unresolvable");
            return [];
        }

        var serverTools = await DiscoverAsync(mcpTemplate, tool.Binding.DiscoveryCacheMinutes ?? 10, cancellationToken);
        var prefix = tool.Binding.Prefix ?? string.Empty;
        var definitions = new List<AgentToolDefinition>();

        foreach (var serverTool in serverTools.Where(t => allow.Contains(t.Name)))
        {
            var exposed = prefix + serverTool.Name;
            var parameters = serverTool.InputSchema.ValueKind == JsonValueKind.Object ? serverTool.InputSchema : EmptyObjectSchema;
            var original = serverTool.Name;

            Func<AgentToolInvocation, CancellationToken, Task<AgentToolResult>> body = ctx.Stub
                ? StubBody(parameters)
                : async (invocation, ct) =>
                {
                    var prepared = mcpTemplate.CloneTyped();
                    prepared.SetOperation(McpOperations.CallTool);
                    prepared.SetTool(original);
                    prepared.SetArgumentsElement(invocation.Arguments);
                    return await RunTaskAsync(ctx, reference, prepared, null, invocation, ct);
                };

            var validate = ArgumentCheck(parameters);
            definitions.Add(new AgentToolDefinition(
                exposed,
                serverTool.Description ?? exposed,
                parameters,
                ctx.Recorder.Wrap(exposed, AgentToolKind.Mcp, $"sys-tasks/{reference.Domain}/{reference.Key}@{reference.Version}",
                    parameters, tool.TimeoutSeconds, tool.ResultMaxChars, body, validate),
                tool.RequiresApproval)
            {
                ValidateArguments = validate
            });
        }

        return definitions;
    }

    private async Task<IReadOnlyList<McpToolInfo>> DiscoverAsync(McpTask template, int cacheMinutes, CancellationToken cancellationToken)
    {
        var key = $"{template.Domain}:{template.Key}:{template.Version}:{template.Endpoint}";
        if (McpToolMemo.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Tools;

        await using var scope = scopeFactory.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<McpTaskExecutor>();
        var listing = template.CloneTyped();
        listing.SetOperation(McpOperations.ListTools);

        var call = await executor.CallAsync(listing, cancellationToken);
        if (!call.IsSuccess)
        {
            logger.McpCallFailed(McpOperations.ListTools, template.Key, call.Error ?? "unknown");
            return [];
        }

        var tools = call.Result.Tools ?? [];
        McpToolMemo[key] = (DateTime.UtcNow.AddMinutes(Math.Max(0, cacheMinutes)), tools);
        return tools;
    }
}
