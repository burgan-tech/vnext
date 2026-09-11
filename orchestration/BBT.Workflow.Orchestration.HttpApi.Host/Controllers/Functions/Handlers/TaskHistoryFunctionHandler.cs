using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Instances;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>task-history</c> system function: the instance's task execution journal in
/// execution order, metadata only — journaled payloads stay on the Monitor API.
/// </summary>
public sealed class TaskHistoryFunctionHandler(
    IInstanceQueryAppService queryAppService,
    ICallerRoleResolver callerRoleResolver) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.TaskHistory;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        // Same provider-resolved roles as the state function, so the queryRoles gate evaluates the
        // caller consistently across the surfaces that describe one instance.
        var callerRoles = await callerRoleResolver.ResolveRolesAsync(request.Headers, cancellationToken);
        if (!callerRoles.IsSuccess)
            return Result.Fail(callerRoles.Error).ToActionResult(request.HttpContext);

        var input = new GetInstanceTasksInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance,
            Headers = request.Headers,
            QueryParameters = request.QueryParameters,
            Roles = callerRoles.Value
        };

        var result = await queryAppService.GetInstanceTasksAsync(input, cancellationToken);
        return result.ToActionResult(request.HttpContext);
    }
}
