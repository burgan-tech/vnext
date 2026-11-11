using System.Text.Json;
using BBT.Workflow.Agents;
using BBT.Workflow.Instances.Agents;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Agents;

/// <summary>
/// The dry-run answer for an approval stop (issue #1105): the held tool calls are listed and the stop is not
/// reported as a failure; any other status keeps its failure text and lists nothing.
/// </summary>
public class AgentDryRunOutcomeTests
{
    private static readonly JsonElement Arguments = JsonDocument.Parse("""{"orderId":"o-1","amount":120}""").RootElement;

    [Fact]
    public void Approval_stop_lists_the_held_calls_and_has_no_failure_reason()
    {
        var result = new AgentRunResult
        {
            Status = AgentRunStatus.ApprovalRequested,
            FailureReason = "1 tool call(s) wait for approval",
            PendingApprovals =
            [
                new AgentPendingApproval("r-1", "c-1", "issue_refund", Arguments, true),
                new AgentPendingApproval("r-2", "c-2", "lookup_order", Arguments, false)
            ]
        };

        AgentDryRunAppService.FailureReasonOf(result).ShouldBeNull();
        var pending = AgentDryRunAppService.PendingApprovalsOf(result);
        pending.Count.ShouldBe(2);
        pending[0].ShouldBe(new AgentDryRunPendingApproval("r-1", "c-1", "issue_refund", Arguments, true));
        pending[1].Tool.ShouldBe("lookup_order");
        pending[1].RequiresConfirmation.ShouldBeFalse();
    }

    [Theory]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Timeout)]
    public void Other_stops_keep_the_failure_reason_and_list_nothing(AgentRunStatus status)
    {
        var result = AgentRunResult.Failed(status, "provider error");

        AgentDryRunAppService.FailureReasonOf(result).ShouldBe("provider error");
        AgentDryRunAppService.PendingApprovalsOf(result).ShouldBeEmpty();
    }

    [Fact]
    public void Pending_approvals_serialize_under_the_agent_field_names()
    {
        var output = new AgentDryRunOutput("ApprovalRequested", null, null, null, null, [], 0, 0, 0)
        {
            PendingApprovals = [new AgentDryRunPendingApproval("r-1", "c-1", "issue_refund", Arguments, true)]
        };

        var json = JsonSerializer.SerializeToElement(output, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var item = json.GetProperty("pendingApprovals")[0];
        item.GetProperty("tool").GetString().ShouldBe("issue_refund");
        item.GetProperty("arguments").GetProperty("amount").GetInt32().ShouldBe(120);
        item.GetProperty("requiresConfirmation").GetBoolean().ShouldBeTrue();
    }
}
