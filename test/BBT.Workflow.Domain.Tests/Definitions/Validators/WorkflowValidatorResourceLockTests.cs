using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Validators;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Domain.Tests.Definitions.Validators;

/// <summary>
/// Publish-time checks around resource locks and the error boundaries that route their failures.
/// Each one covers a mistake that is otherwise only found in production, as an instance that is
/// stuck, faulted, or quietly unprotected.
/// </summary>
public class WorkflowValidatorResourceLockTests : DomainTestBase<DomainEntryPoint>
{
    private readonly WorkflowValidator _validator = new();

    /// <summary>
    /// A lock whose TTL has already elapsed protects nothing: the key it hands out is expired, so a
    /// second instance walks straight into the section the lock exists to serialize.
    /// </summary>
    [Fact]
    public void Validate_ShouldFail_WhenAcquireLockHasNonPositiveTtl()
    {
        var workflow = CreateWorkflow(lockTtlSeconds: 0);

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        var error = result.ValidationErrors
            .Where(e => e.ErrorMessage!.Contains("ttlSeconds greater than 0"))
            .ToList()
            .ShouldHaveSingleItem();
        error.MemberNames.ShouldContain("Workflow.States[working].Transitions[acquire].ResourceLock.TtlSeconds");
    }

    [Fact]
    public void Validate_ShouldPass_WhenAcquireLockHasPositiveTtl()
    {
        var workflow = CreateWorkflow(lockTtlSeconds: 300);

        var result = _validator.Validate(workflow);

        result.ValidationErrors.ShouldNotContain(e => e.ErrorMessage!.Contains("ttlSeconds greater than 0"));
    }

    /// <summary>
    /// Release carries no TTL, so a zero there is not a missing safety net — it is an unused field.
    /// </summary>
    [Fact]
    public void Validate_ShouldPass_WhenReleaseLockHasZeroTtl()
    {
        var workflow = CreateWorkflow(lockTtlSeconds: 0, lockAction: "Release");

        var result = _validator.Validate(workflow);

        result.ValidationErrors.ShouldNotContain(e => e.ErrorMessage!.Contains("ttlSeconds greater than 0"));
    }

    /// <summary>
    /// A state boundary reroutes an instance that never left its state, so it may only name a
    /// transition reachable from there. Naming one that belongs elsewhere strands the instance with
    /// nothing saying why.
    /// </summary>
    [Fact]
    public void Validate_ShouldFail_WhenStateBoundaryRoutesToATransitionOfAnotherState()
    {
        var workflow = CreateWorkflow(boundaryTransition: "elsewhere");

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        var error = result.ValidationErrors
            .Where(e => e.ErrorMessage!.Contains("not available from that state"))
            .ToList()
            .ShouldHaveSingleItem();
        error.ErrorMessage.ShouldContain("'elsewhere'");
    }

    /// <summary>
    /// The morph-idm shape: a shared transition whose availableIn names the state carrying the
    /// boundary. This is the wiring the queue depends on, so it must stay valid.
    /// </summary>
    [Fact]
    public void Validate_ShouldPass_WhenStateBoundaryRoutesToASharedTransitionAvailableThere()
    {
        var workflow = CreateWorkflow(boundaryTransition: "park");

        var result = _validator.Validate(workflow);

        result.ValidationErrors.ShouldNotContain(e => e.ErrorMessage!.Contains("not available from that state"));
    }

    /// <summary>
    /// The ErrorBoundary pipeline profile skips the resource-lock step, so a lock on a transition
    /// reached through a boundary never runs. The definition reads as protected and is not — a
    /// warning, since the routing itself still works.
    /// </summary>
    [Fact]
    public void Validate_ShouldWarn_WhenBoundaryTargetTransitionDeclaresALock()
    {
        var workflow = CreateWorkflow(boundaryTransition: "park", lockOnParkTransition: true);

        var result = _validator.Validate(workflow);

        var warning = result.Warnings
            .Where(w => w.ErrorMessage!.Contains("skips the resource-lock step"))
            .ToList()
            .ShouldHaveSingleItem();
        warning.ErrorMessage.ShouldContain("'park'");
        // A warning must not block the publish.
        result.ValidationErrors.ShouldNotContain(e => e.ErrorMessage!.Contains("skips the resource-lock step"));
    }

    [Fact]
    public void Validate_ShouldNotWarn_WhenBoundaryTargetTransitionHasNoLock()
    {
        var workflow = CreateWorkflow(boundaryTransition: "park");

        var result = _validator.Validate(workflow);

        result.Warnings.ShouldNotContain(w => w.ErrorMessage!.Contains("skips the resource-lock step"));
    }

    /// <summary>
    /// Retry re-runs the step while the lock is still held by somebody else: every attempt fails the
    /// same way and the instance faults anyway, just later. The failure needs a different route.
    /// </summary>
    [Theory]
    [InlineData("Execution:200010")]
    [InlineData("Execution:200014")]
    public void Validate_ShouldFail_WhenBoundaryRetriesAResourceLockConflict(string errorCode)
    {
        var workflow = CreateWorkflow(
            boundaryTransition: null,
            boundaryErrorCode: errorCode,
            boundaryAction: "Retry");

        var result = _validator.Validate(workflow);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("Retry cannot resolve a resource-lock conflict"));
    }

    /// <summary>
    /// Retry stays legitimate for everything else — the check must not spill over onto ordinary rules.
    /// </summary>
    [Fact]
    public void Validate_ShouldPass_WhenBoundaryRetriesANonLockError()
    {
        var workflow = CreateWorkflow(
            boundaryTransition: null,
            boundaryErrorCode: "Task:400007",
            boundaryAction: "Retry");

        var result = _validator.Validate(workflow);

        result.ValidationErrors.ShouldNotContain(e =>
            e.ErrorMessage!.Contains("Retry cannot resolve a resource-lock conflict"));
    }

    /// <summary>
    /// Builds a two-state workflow shaped like the limit-lock queue: <c>working</c> acquires a lock and
    /// carries a boundary, <c>park</c> is a shared transition available there, and <c>elsewhere</c>
    /// belongs to another state so it can stand in for a mis-wired boundary target.
    /// </summary>
    private static WorkflowDefinition CreateWorkflow(
        int lockTtlSeconds = 300,
        string lockAction = "Acquire",
        string? boundaryTransition = "park",
        string boundaryErrorCode = "Execution:200010",
        string boundaryAction = "Rollback",
        bool lockOnParkTransition = false)
    {
        const string script = """{ "location": "./src/Key.csx", "code": "cmV0dXJuICJrIjs=" }""";
        var transitionLock =
            $$"""
            "resourceLock": { "keyExpression": {{script}}, "action": "{{lockAction}}", "ttlSeconds": {{lockTtlSeconds}}, "onConflict": "Abort" }
            """;
        var parkLock = lockOnParkTransition
            ? $$"""
              , "resourceLock": { "keyExpression": {{script}}, "action": "Acquire", "ttlSeconds": 60, "onConflict": "Abort" }
              """
            : string.Empty;
        var ruleTransition = boundaryTransition is null
            ? string.Empty
            : $$""" , "transition": "{{boundaryTransition}}" """;

        var json =
            $$"""
            {
                "type": "F",
                "labels": [{"label": "Lock", "language": "en"}],
                "startTransition": {
                    "key": "start",
                    "target": "begin",
                    "triggerType": "manual",
                    "versionStrategy": "Minor",
                    "labels": [{"label": "Start", "language": "en"}],
                    "onExecutionTasks": []
                },
                "sharedTransitions": [
                    {
                        "key": "park",
                        "target": "parked",
                        "triggerType": "manual",
                        "versionStrategy": "Minor",
                        "availableIn": ["working"],
                        "labels": [{"label": "Park", "language": "en"}]
                        {{parkLock}}
                    }
                ],
                "states": [
                    {
                        "key": "begin",
                        "stateType": "initial",
                        "labels": [{"label": "Begin", "language": "en"}],
                        "transitions": [
                            {
                                "key": "elsewhere",
                                "target": "working",
                                "triggerType": "manual",
                                "versionStrategy": "Minor",
                                "labels": [{"label": "Elsewhere", "language": "en"}]
                            }
                        ]
                    },
                    {
                        "key": "working",
                        "stateType": "intermediate",
                        "labels": [{"label": "Working", "language": "en"}],
                        "errorBoundary": {
                            "onError": [
                                {
                                    "errorCodes": ["{{boundaryErrorCode}}"],
                                    "action": "{{boundaryAction}}",
                                    "priority": 10
                                    {{ruleTransition}}
                                }
                            ]
                        },
                        "transitions": [
                            {
                                "key": "acquire",
                                "target": "parked",
                                "triggerType": "manual",
                                "versionStrategy": "Minor",
                                "labels": [{"label": "Acquire", "language": "en"}],
                                {{transitionLock}}
                            }
                        ]
                    },
                    {
                        "key": "parked",
                        "stateType": "finish",
                        "labels": [{"label": "Parked", "language": "en"}]
                    }
                ]
            }
            """;

        return JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonSerializerConstants.JsonOptions)!;
    }
}
