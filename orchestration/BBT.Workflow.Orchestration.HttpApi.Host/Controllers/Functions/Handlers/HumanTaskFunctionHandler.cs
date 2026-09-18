using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Instances;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>human-task</c> function at domain level.
/// Returns active instances with Human state subtype assigned to the current user.
/// </summary>
public sealed class HumanTaskFunctionHandler(
    IInstanceQueryAppService queryAppService) : IDomainFunctionHandler
{
    /// <summary>
    /// Signals that a per-schema limit or the merged result cap cut the list. A header rather than
    /// a body field: the response is a bare JSON array and the consumer deserializes it as a list,
    /// so an envelope would be a breaking change. Truncation must still be visible — a silently
    /// capped task list is a wrong answer, not a shorter one.
    /// </summary>
    public const string TruncatedHeader = "X-VNext-HumanTask-Truncated";

    /// <summary>
    /// Asks for the response to be rebuilt rather than served from the cache, for the moments a
    /// client cannot tolerate the TTL's staleness. It skips the cache READ only: the rebuilt result
    /// is still stored, behind the same single-flight gate as an ordinary miss, so the header cannot
    /// be used to make this endpoint cheaper to attack than it is with no cache at all.
    /// Honoured only while <c>HumanTaskFunctionCache:AllowClientOverride</c> is true.
    /// </summary>
    public const string CacheOverrideHeader = "X-VNext-Cache-Override";

    public string FunctionType => FunctionTypeConst.HumanTask;

    public async Task<IActionResult> HandleAsync(
        DomainFunctionRequest request, CancellationToken cancellationToken)
    {
        var result = await queryAppService.GetHumanTaskInstancesAsync(
            request.Domain, request.Headers, ReadCacheOverride(request), cancellationToken);

        if (!result.IsSuccess || result.Value is null)
            return Result<List<HumanTaskItemOutput>>.Fail(result.Error).ToActionResult(request.HttpContext);

        if (result.Value.Truncated)
            request.HttpContext.Response.Headers[TruncatedHeader] = "true";

        return Result<List<HumanTaskItemOutput>>.Ok(result.Value.Items)
            .ToActionResult(request.HttpContext);
    }

    /// <summary>
    /// Reads the override header. Anything that is not an explicit affirmative is false — a
    /// malformed value must not silently turn into a rebuild request.
    /// </summary>
    private static bool ReadCacheOverride(DomainFunctionRequest request)
    {
        if (request.Headers is null
            || !request.Headers.TryGetValue(CacheOverrideHeader, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return bool.TryParse(raw, out var parsed)
            ? parsed
            : string.Equals(raw, "1", StringComparison.Ordinal);
    }
}
