using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Declarative;
using BBT.Workflow.Scripting.Expressions;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances;

/// <inheritdoc cref="IWorkflowOutputMappingService" />
/// <remarks>
/// <para>
/// Two shapes of the workflow <c>output</c>: a script (<see cref="IOutputHandler"/>) or, since Phase C2, a declarative
/// block (<see cref="Definitions.Workflow.OutputBlock"/>). A stored definition holds one or the other (same JSON key).
/// Both run under the same <c>Script.Execute</c> span (kind <c>workflowOutput</c>) and share the failure path: the
/// failure is logged (<c>WorkflowOutputScriptFailed</c>), the span is marked as an error and the caller keeps the
/// standard envelope. Registered scoped.
/// </para>
/// </remarks>
/// <param name="scriptEngine">Compiles the <c>output</c> script.</param>
/// <param name="logger">Logger for output mapping failures.</param>
/// <param name="callerAccessorFactory">Resolves the hop's caller for the block's <c>user</c>; no caller when null.</param>
/// <param name="expressionCompiler">Compiler for the block's expressions; the shared one when null.</param>
/// <param name="clock">Source of the block's <c>now</c>; the system clock when null.</param>
public sealed class WorkflowOutputMappingService(
    IScriptEngine scriptEngine,
    ILogger<WorkflowOutputMappingService> logger,
    ICallerAccessorFactory? callerAccessorFactory = null,
    IExpressionCompiler? expressionCompiler = null,
    TimeProvider? clock = null)
    : IWorkflowOutputMappingService
{
    private const string ScriptKind = "workflowOutput";
    private const string OutputPhase = "output";
    private const string DataMember = "data";
    private const string StatusCodeMember = "statusCode";
    private const string HeadersMember = "headers";

    private readonly IExpressionCompiler _compiler = expressionCompiler ?? ExpressionCompiler.Shared;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<Result<WorkflowOutputResult?>> ApplyAsync(
        Definitions.Workflow workflow,
        ScriptContext scriptContext,
        WorkflowOutputHop? hop,
        CancellationToken cancellationToken = default)
    {
        if (workflow.Output is { HasMappingCode: true } script)
            return await ApplyScriptAsync(workflow, script, scriptContext, cancellationToken);

        if (workflow.OutputBlock is { } block)
            return await ApplyBlockAsync(workflow, block, scriptContext, hop, cancellationToken);

        // Not configured → signal "no output" so the caller keeps the standard envelope.
        return Result<WorkflowOutputResult?>.Ok(null);
    }

    private async Task<Result<WorkflowOutputResult?>> ApplyScriptAsync(
        Definitions.Workflow workflow,
        ScriptCode script,
        ScriptContext scriptContext,
        CancellationToken cancellationToken)
    {
        using var activity = ScriptActivityHelper.StartExecuteActivity(ScriptKind);

        try
        {
            var handler = await scriptEngine.CompileToInstanceAsync<IOutputHandler>(
                script,
                flowScripts: workflow.Scripts,
                cancellationToken: cancellationToken);

            var response = await handler.OutputHandler(scriptContext);

            // Output ran — Data may legitimately be null (intentional empty response).
            return Result<WorkflowOutputResult?>.Ok(new WorkflowOutputResult(
                (object?)response.Data,
                response.StatusCode,
                NormalizeHeaders((object?)response.Headers)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller itself went away — propagate rather than reporting a script failure and
            // silently degrading to the standard envelope.
            throw;
        }
        catch (Exception ex)
        {
            return Fallback(workflow, activity, ex);
        }
    }

    /// <summary>
    /// Runs the declarative <c>output</c> block into an empty document <c>{ data, statusCode, headers }</c>. Variables:
    /// <c>data</c> (latest data as stored), <c>input</c> (the hop's request payload), <c>instance</c>, <c>now</c>,
    /// <c>user</c> (the hop's caller), <c>context</c>, <c>headers</c>. Any failure — a plan that does not build, a
    /// failing expression, a document of the wrong shape — takes the script's failure path.
    /// </summary>
    private async Task<Result<WorkflowOutputResult?>> ApplyBlockAsync(
        Definitions.Workflow workflow,
        MappingBlock block,
        ScriptContext scriptContext,
        WorkflowOutputHop? hop,
        CancellationToken cancellationToken)
    {
        using var activity = ScriptActivityHelper.StartExecuteActivity(ScriptKind);

        try
        {
            var plan = DeclarativePlanCache.GetOrBuild(_compiler, block, DeclarativeSiteKind.WorkflowOutput);
            if (!plan.IsSuccess)
                return Fallback(workflow, activity, BlockFailure(plan.Error));

            scriptContext.BindHopInput(hop?.Input);
            var caller = callerAccessorFactory is not null && hop is not null
                ? callerAccessorFactory.ForHop(hop.Actor, hop.Headers, cancellationToken)
                : NullCallerAccessor.Instance;
            var scope = ExpressionScope.Create(scriptContext, _clock, caller, ScopeKind.WorkflowOutput);
            var site = new DeclarativeSite(workflow.Key, OutputPhase)
            {
                Logger = logger,
                ErrorSubject = $"output of workflow '{workflow.Key}'"
            };

            var document = new JsonObject();
            var applied = await DeclarativeBlockEngine.ApplyAsync(
                plan.Value!, scope, document, result: null, site, cancellationToken);
            if (!applied.IsSuccess)
                return Fallback(workflow, activity, BlockFailure(applied.Error));

            var output = ToOutputResult(document, site);
            return output.IsSuccess
                ? Result<WorkflowOutputResult?>.Ok(output.Value)
                : Fallback(workflow, activity, BlockFailure(output.Error));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fallback(workflow, activity, ex);
        }
    }

    /// <summary>
    /// Converts the block's document to the response: <c>data</c> (absent → empty response), <c>statusCode</c> (an
    /// integer JSON number, or absent) and <c>headers</c> (an object of string values, or absent).
    /// </summary>
    private static Result<WorkflowOutputResult> ToOutputResult(JsonObject document, DeclarativeSite site)
    {
        // An owned element: the response body serializes as the JSON the block wrote.
        object? data = document[DataMember] is { } dataNode ? JsonSerializer.SerializeToElement(dataNode) : null;

        int? statusCode = null;
        switch (document[StatusCodeMember])
        {
            case null:
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var code):
                statusCode = code;
                break;
            case JsonNode other:
                return Result<WorkflowOutputResult>.Fail(
                    site.Failure($"$.statusCode is a JSON {other.GetValueKind()} ({other.ToJsonString()}), not an integer."));
        }

        Dictionary<string, string>? headers = null;
        switch (document[HeadersMember])
        {
            case null:
                break;
            case JsonObject members:
                headers = new Dictionary<string, string>(members.Count, StringComparer.Ordinal);
                foreach (var (name, node) in members)
                {
                    if (node is not JsonValue headerValue || !headerValue.TryGetValue<string>(out var text))
                    {
                        return Result<WorkflowOutputResult>.Fail(site.Failure(
                            $"$.headers.{name} is a JSON {node?.GetValueKind() ?? JsonValueKind.Null}, not a string."));
                    }

                    headers[name] = text;
                }

                break;
            case JsonNode other:
                return Result<WorkflowOutputResult>.Fail(
                    site.Failure($"$.headers is a JSON {other.GetValueKind()}, not an object."));
        }

        return Result<WorkflowOutputResult>.Ok(
            new WorkflowOutputResult(data, statusCode, headers is { Count: > 0 } ? headers : null));
    }

    /// <summary>A block failure as the exception the script failure path logs.</summary>
    private static InvalidOperationException BlockFailure(Error error) => new(error.Message ?? error.Code);

    /// <summary>
    /// The shared failure path: logged, the span marked as an error, and <c>null</c> so the caller degrades to the
    /// standard envelope (non-blocking).
    /// </summary>
    private Result<WorkflowOutputResult?> Fallback(Definitions.Workflow workflow, Activity? activity, Exception ex)
    {
        logger.WorkflowOutputScriptFailed(workflow.Key, ex);
        activity.SetError(ex);
        return Result<WorkflowOutputResult?>.Ok(null);
    }

    /// <summary>
    /// Converts the script's dynamic Headers value to a string dictionary,
    /// mirroring <c>FunctionAppService.NormalizeHeaders</c>.
    /// </summary>
    private static Dictionary<string, string>? NormalizeHeaders(object? value)
    {
        if (value is null)
            return null;

        try
        {
            var headers = value switch
            {
                Dictionary<string, string> typedHeaders => typedHeaders,
                IDictionary<string, object?> objectHeaders => objectHeaders
                    .Where(header => header.Value != null)
                    .ToDictionary(header => header.Key, header => Convert.ToString(header.Value, CultureInfo.InvariantCulture) ?? string.Empty),
                _ => JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(value))
            };

            return headers is { Count: > 0 } ? headers : null;
        }
        catch
        {
            return null;
        }
    }
}
