using System.Text.Json;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution;
using Shouldly;
using Xunit;
using DefWorkflow = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Validation;

/// <summary>
/// Pins the ownership rule for payload-schema validation: exactly one place validates a request's
/// payload, and the marker that says "already validated" never survives a hop.
/// <para>
/// The intake used to validate too, which made every transition request resolve its schema twice
/// and build an execution context it then discarded. Validation now belongs to the execution entry
/// (the async strategy before it enqueues, the pipeline on the sync path); only START validates
/// earlier, because it must do so before the instance row is persisted, and it says so with
/// <see cref="WorkflowExecutionContext.PayloadSchemaValidated"/>.
/// </para>
/// </summary>
public sealed class PayloadSchemaValidationOwnershipTests
{
    [Fact]
    public void APlainRequestClaimsNothing()
    {
        // Default false: a transition request arrives unvalidated, so the execution entry it
        // reaches is the one that validates it.
        new WorkflowExecutionContext().PayloadSchemaValidated.ShouldBeFalse();
    }

    [Fact]
    public void TheClaimNeverCrossesAHop()
    {
        // A job payload carrying "already validated" would let a hop skip validation on the
        // strength of a decision made about a different request.
        var context = new WorkflowExecutionContext
        {
            Domain = "core",
            WorkflowKey = "login-flow",
            TransitionKey = "start-login",
            PayloadSchemaValidated = true,
            ResolvedWorkflow = DefWorkflow.Create()
        };

        JsonSerializer.Serialize(context).ShouldNotContain("PayloadSchemaValidated");
        typeof(TransitionJobPayload).GetProperty("PayloadSchemaValidated").ShouldBeNull();
    }
}
