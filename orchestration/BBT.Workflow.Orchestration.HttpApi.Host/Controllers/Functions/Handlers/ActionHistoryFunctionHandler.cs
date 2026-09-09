using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions.Functions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>action-history</c> system function: the recorded execution sub-steps of one task
/// journal row, addressed by the <c>taskId</c> query parameter (a row id from the task-history
/// function). Answers 404 (<c>Instance:100038</c>) when the task is not the instance's own.
/// </summary>
public sealed class ActionHistoryFunctionHandler(
    IInstanceQueryAppService queryAppService,
    ICallerRoleResolver callerRoleResolver) : IInstanceFunctionHandler
{
    public string FunctionType => FunctionTypeConst.ActionHistory;

    public async Task<IActionResult> HandleAsync(
        InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.QueryParameters.GetValueOrDefault("taskId"), out var taskId))
            return Result.Fail(WorkflowErrors.InstanceTaskIdRequired()).ToActionResult(request.HttpContext);

        var callerRoles = await callerRoleResolver.ResolveRolesAsync(request.Headers, cancellationToken);
        if (!callerRoles.IsSuccess)
            return Result.Fail(callerRoles.Error).ToActionResult(request.HttpContext);

        var input = new GetInstanceTaskActionsInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance,
            TaskId = taskId,
            Headers = request.Headers,
            QueryParameters = request.QueryParameters,
            Roles = callerRoles.Value
        };

        var result = await queryAppService.GetInstanceTaskActionsAsync(input, cancellationToken);
        return result.ToActionResult(request.HttpContext);
    }
}
