using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Agents;
using BBT.Workflow.Definitions.Functions;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Handles the <c>agent-runs</c> system function (issue #1105): the instance's AI agent runs, newest first,
/// metadata only. <c>?runId=</c> returns one run with its tool calls; <c>?state=</c>, <c>?skip=</c> and
/// <c>?take=</c> page the listing. Like <c>tasks</c>, it has no role gate of its own — <c>queryRoles</c> is
/// answered by <c>authorize?queryRoles=true</c>.
/// </summary>
public sealed class AgentRunsFunctionHandler(IAgentRunQueryAppService queryAppService) : IInstanceFunctionHandler
{
    /// <inheritdoc />
    public string FunctionType => FunctionTypeConst.AgentRuns;

    /// <inheritdoc />
    public async Task<IActionResult> HandleAsync(InstanceFunctionRequest request, CancellationToken cancellationToken)
    {
        Guid? runId = null;
        if (request.QueryParameters.GetValueOrDefault("runId") is { Length: > 0 } rawRunId)
        {
            if (!Guid.TryParse(rawRunId, out var parsed))
            {
                return Result.Fail(Error.Validation(WorkflowErrorCodes.AgentRunIdInvalid,
                    "Query parameter \"runId\" must be an agent run id (GUID) from the agent-runs function.", target: "runId"))
                    .ToActionResult(request.HttpContext);
            }

            runId = parsed;
        }

        var input = new GetAgentRunsInput
        {
            Domain = request.Domain,
            Workflow = request.Workflow,
            Instance = request.Instance,
            RunId = runId,
            State = request.QueryParameters.GetValueOrDefault("state"),
            Skip = int.TryParse(request.QueryParameters.GetValueOrDefault("skip"), out var skip) ? skip : 0,
            Take = int.TryParse(request.QueryParameters.GetValueOrDefault("take"), out var take) ? take : 20
        };

        var result = await queryAppService.GetAsync(input, cancellationToken);
        return result.ToActionResult(request.HttpContext);
    }
}
