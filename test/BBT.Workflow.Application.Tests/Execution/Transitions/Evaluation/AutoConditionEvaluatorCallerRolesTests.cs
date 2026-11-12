using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Evaluation;

/// <summary>
/// Automatic-transition rules and <c>context.Caller</c> (council 2026-11-12-script-caller-roles-background,
/// Chair override): roles on caller-driven hops only, exposed for the rule and never left on the hop's shared
/// context, and a provider failure fails the evaluation (110006) instead of falling through.
/// </summary>
public sealed class AutoConditionEvaluatorCallerRolesTests
{
    private readonly ITaskConditionService _conditions = Substitute.For<ITaskConditionService>();
    private readonly ICallerRoleResolver _resolver = Substitute.For<ICallerRoleResolver>();
    private readonly AutoConditionEvaluator _evaluator;
    private readonly ScriptContext _shared = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();
    private readonly Transition _rule;

    public AutoConditionEvaluatorCallerRolesTests()
    {
        _evaluator = new AutoConditionEvaluator(
            _conditions,
            Substitute.For<IScriptContextFactory>(),
            Substitute.For<IInstanceRepository>(),
            NullLogger<AutoConditionEvaluator>.Instance,
            Substitute.For<IRuntimeInfoProvider>(),
            new CallerAccessorFactory(_resolver, NullLogger<CallerAccessorFactory>.Instance));
        _rule = Transition.Create("to-approved", "review", "approved", TriggerType.Automatic, "Patch");
        _rule.SetRule("./IsApprover.csx", "return true;");
    }

    [Fact]
    public async Task UserHop_RuleReadsRoles_SeesTheCallersRoles_AndTheSharedContextIsLeftClean()
    {
        Roles(new CallerRoleResolution(["approver"], CallerRoleOutcome.Resolved));
        RuleReadsRole("approver");

        var result = await _evaluator.EvaluateAsync(_rule, Context(ExecutionActor.User), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(AutoConditionStatus.Satisfied);
        // The same context serves the hop's task mappings next: they must not see the caller.
        _shared.Caller.ShouldBeSameAs(NullCallerAccessor.Instance);
    }

    [Fact]
    public async Task SystemHop_HasNoCaller_AndMakesNoProviderCall()
    {
        bool? available = null;
        _conditions.ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => { available = ci.Arg<ScriptContext>().Caller.IsAvailable; return Result<bool>.Ok(false); });

        var result = await _evaluator.EvaluateAsync(_rule, Context(ExecutionActor.System), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        available.ShouldBe(false);
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    [Fact]
    public async Task ProviderFailed_RuleSwallowsAndReturnsFalse_FailsWith110006_InsteadOfFallingThrough()
    {
        Roles(new CallerRoleResolution([], CallerRoleOutcome.Failed, "timeout"));
        _conditions.ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                try { await ci.Arg<ScriptContext>().Caller.HasRoleAsync("approver"); }
                catch (CallerRolesUnavailableException) { /* careless rule */ }
                return Result<bool>.Ok(false);
            });

        var result = await _evaluator.EvaluateAsync(_rule, Context(ExecutionActor.User), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.CallerRolesUnavailable);
    }

    [Fact]
    public async Task ProviderDown_RuleNotReadingRoles_IsUnaffected()
    {
        Roles(new CallerRoleResolution([], CallerRoleOutcome.Failed, "timeout"));
        _conditions.ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(true));

        var result = await _evaluator.EvaluateAsync(_rule, Context(ExecutionActor.User), CancellationToken.None);

        result.Value!.Status.ShouldBe(AutoConditionStatus.Satisfied);
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);
    }

    private void Roles(CallerRoleResolution resolution) =>
        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(resolution));

    private void RuleReadsRole(string role) =>
        _conditions.ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(async ci => Result<bool>.Ok(await ci.Arg<ScriptContext>().Caller.HasRoleAsync(role)));

    private TransitionExecutionContext Context(ExecutionActor actor)
    {
        var context = new TransitionExecutionContext
        {
            InstanceId = Guid.NewGuid(),
            Domain = "d",
            WorkflowKey = "w",
            TransitionKey = "submit",
            Trigger = actor == ExecutionActor.User ? TriggerType.Manual : TriggerType.Scheduled,
            Actor = actor,
            CorrelationId = "c",
            ExecutionChainId = "e",
            RequestedAt = DateTimeOffset.UtcNow,
            Headers = new Dictionary<string, string?> { ["sub"] = "u1", ["act_sub"] = "u1" },
            TraceId = "t",
            SpanId = "s",
        };
        context.Cache["ScriptContext"] = _shared;
        return context;
    }
}
