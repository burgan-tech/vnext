using System.Linq;
using System.Text.Json;
using BBT.Workflow.Agents;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Agents;

/// <summary>Reading held tool calls from the <c>$agent</c> summary and pairing them with the answer body.</summary>
public class AgentApprovalSupportTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement Data = Json("""
        { "triage": { "$agent": { "state": "assessing", "status": "ApprovalRequested", "pendingApprovals": [
            { "requestId": "r1", "callId": "c1", "tool": "pay", "arguments": { "amount": 10 }, "requiresConfirmation": true },
            { "requestId": "r2", "callId": "c2", "tool": "lookup", "arguments": {}, "requiresConfirmation": false },
            { "requestId": "r3", "callId": "c3", "tool": "refund", "arguments": {}, "requiresConfirmation": true } ] } } }
        """);

    [Fact]
    public void Pending_calls_are_read_for_the_owning_state_only()
    {
        AgentApprovalSupport.ReadPending(Data, "$.triage", "assessing").Count.ShouldBe(3);
        AgentApprovalSupport.ReadPending(Data, "$.triage", "other").ShouldBeEmpty();
        AgentApprovalSupport.ReadPending(Json("""{ "triage": { "$agent": { "state": "assessing", "status": "Completed", "pendingApprovals": [] } } }"""),
            "$.triage", "assessing").ShouldBeEmpty();
    }

    [Fact]
    public void Answers_match_by_call_id_then_fall_back_to_an_answer_without_one()
    {
        var pending = AgentApprovalSupport.ReadPending(Data, "$.triage", "assessing");
        var answers = AgentApprovalSupport.MatchAnswers(pending, Json("""
            { "approval": [ { "callId": "c1", "approved": false, "reason": "too much" }, { "approved": true } ] }
            """));

        answers.Single(a => a.Pending.CallId == "c1").Approved.ShouldBeFalse();
        answers.Single(a => a.Pending.CallId == "c1").Reason.ShouldBe("too much");
        answers.Single(a => a.Pending.CallId == "c3").Approved.ShouldBeTrue();
        answers.Single(a => a.Pending.CallId == "c2").Approved.ShouldBeTrue();
    }

    [Fact]
    public void A_held_call_without_an_answer_is_denied_and_an_unconfirmed_one_is_approved()
    {
        var pending = AgentApprovalSupport.ReadPending(Data, "$.triage", "assessing");
        var answers = AgentApprovalSupport.MatchAnswers(pending, Json("""{ "approval": { "approved": false } }"""));

        answers.Single(a => a.Pending.CallId == "c1").Approved.ShouldBeFalse();
        answers.Single(a => a.Pending.CallId == "c2").Approved.ShouldBeTrue();

        var unanswered = AgentApprovalSupport.MatchAnswers(pending, Json("{}"));
        unanswered.Single(a => a.Pending.CallId == "c3").Reason.ShouldBe("no approval answer was given");
    }
}
