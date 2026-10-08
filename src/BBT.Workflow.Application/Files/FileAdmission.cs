using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Transitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Files;

/// <summary>
/// The x-storage file swap at transition admission (sync pipeline and async accept).
/// </summary>
public interface IFileAdmission
{
    /// <summary>
    /// Offloads <c>content</c> in the request payload to the store and replaces it with handles on
    /// <paramref name="context"/>, <paramref name="workflowContext"/> and — for a client (External) payload
    /// only — the request's raw body. A failure leaves all three untouched and is returned as is
    /// (503 FileStoreUnavailable / 400 FileReferenceInvalid / 503 FileSchemaUnavailable).
    /// </summary>
    Task<Result> ApplyAsync(
        TransitionExecutionContext context,
        WorkflowExecutionContext workflowContext,
        CancellationToken cancellationToken);
}

/// <summary>
/// The file swap at transition admission (spec §3: after schema validation, before anything is persisted
/// or enqueued). Skipped for a request the parent relays to its active SubFlow: the leaf records it and
/// swaps it against its own master schema.
/// </summary>
public sealed class FileAdmission(IFileOffloadService offloadService, IRequestRawBodyProvider rawBodyProvider)
    : IFileAdmission
{
    /// <inheritdoc />
    public async Task<Result> ApplyAsync(
        TransitionExecutionContext context,
        WorkflowExecutionContext workflowContext,
        CancellationToken cancellationToken)
    {
        if (SubflowForwardRule.WillForward(context))
            return Result.Ok();

        // Not applicable ⇒ no work and no span (trace-span-tree: non-applicable steps leave no trace).
        var payloadElement = context.DataElement;
        if (payloadElement is not { ValueKind: JsonValueKind.Object })
            return Result.Ok();
        // A master schema that cannot be loaded fails closed (503 FileSchemaUnavailable, before anything is persisted
        // or enqueued) when the payload could carry a file; otherwise the request proceeds with no fields.
        var resolved = FileStorageFields.ForPayload(
            await offloadService.GetFieldsAsync(context.Workflow, cancellationToken), payloadElement.Value);
        if (!resolved.IsSuccess)
            return Result.Fail(resolved.Error);
        var fields = resolved.Value!;
        if (fields.Count == 0)
            return Result.Ok();

        using var activity = PipelineStepActivityHelper.StartTransitionActivity("Files.Offload", context.TransitionKey);
        var trusted = workflowContext.TrustedPayload;
        var result = await offloadService.OffloadAsync(new FileOffloadRequest(
            context.Workflow,
            context.Instance.Id,
            payloadElement,
            context.Instance.LatestData?.Data.JsonElement,
            trusted ? FileOffloadMode.Trusted : FileOffloadMode.External,
            fields), cancellationToken);
        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Code);
            return Result.Fail(result.Error);
        }
        if (!result.Value!.Changed)
            return Result.Ok();

        var payload = result.Value.Payload;
        context.Data = payload;
        if (workflowContext.Data is not null)
            workflowContext.Data.Attributes = payload;
        // Scripts read ScriptContext.RawBody: a client request must expose the handles, never the bytes.
        // A trusted (runtime-produced) hop has no raw body of its own and must not overwrite the outer
        // request's.
        if (!trusted)
            rawBodyProvider.ReplaceRawBodyAttributes(payload);
        return Result.Ok();
    }
}
