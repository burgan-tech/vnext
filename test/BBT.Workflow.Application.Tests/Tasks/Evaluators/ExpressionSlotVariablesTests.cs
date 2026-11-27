using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluators;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Evaluators;

/// <summary>
/// One standard for every Dynamic Expresso slot: the condition rules (<see cref="DynamicExpressoConditionEvaluator"/>)
/// and the string keys (<see cref="DynamicExpressoValueEvaluator"/>) bind <c>data</c>, <c>input</c>, <c>instance</c>,
/// <c>now</c>, <c>user</c> next to the unchanged legacy <c>context</c>. Pins backward compatibility of
/// <c>context</c>-only expressions, each new variable, missing values, the caller-less hop, the provider failure and the
/// lazy binding.
/// </summary>
public sealed class ExpressionSlotVariablesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private const string DataJson = """{"amount":1500,"customer":{"id":"c-42"},"flag":true}""";

    private static ScriptCode Expr(string code) => ScriptCode.FromNative(code, ConditionScriptLocations.DynamicExpresso);

    private static DynamicExpressoConditionEvaluator Condition() =>
        new(NullLogger<DynamicExpressoConditionEvaluator>.Instance, new ExpressionCompiler(64), new FakeTimeProvider(Now));

    private static DynamicExpressoValueEvaluator Key() =>
        new(NullLogger<DynamicExpressoValueEvaluator>.Instance, new ExpressionCompiler(64), new FakeTimeProvider(Now));

    private static Instance InstanceWithData(string json)
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "ctx-key");
        var row = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.0", new JsonData(json), true);
        typeof(InstanceData).GetProperty(nameof(InstanceData.VersionNo))!.SetValue(row, 1L);
        instance.AcceptPersistedData(row);
        return instance;
    }

    private static ScriptContext Context(
        string json = DataJson,
        object? body = null,
        object? transitionInput = null,
        ICallerAccessor? caller = null)
    {
        var builder = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(InstanceWithData(json))
            .SetBody(body ?? new Dictionary<string, object> { ["amount"] = 150000, ["status"] = "x" })
            .SetCaller(caller);
        if (transitionInput is not null)
            builder.SetCurrentTransition(new ScriptTransitionRequest(transitionInput, null));
        return builder.Build();
    }

    private static CallerAccessor Caller(params string[] roles) =>
        new(() => Task.FromResult(new CallerRoleSnapshot(roles, Failed: false, FromHeader: false)));

    private static CallerAccessor FailingCaller() =>
        new(() => Task.FromResult(new CallerRoleSnapshot([], Failed: true, FromHeader: false)));

    // ----- backward compatibility: legacy context-only expressions -------------------------------------------------

    [Theory]
    [InlineData("context.Body[\"amount\"].AsDouble() > 100000", true)]
    [InlineData("context.Body[\"amount\"].AsDouble() > 200000", false)]
    [InlineData("context.Instance.Key == \"ctx-key\"", true)]
    public async Task Condition_LegacyContextExpression_EvaluatesAsBefore(string code, bool expected)
    {
        var result = await Condition().EvaluateAsync(Expr(code), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value.ShouldBe(expected);
    }

    /// <summary>
    /// Parity: the same text through the legacy signature (context only, assignment on) and the new one gives the same
    /// outcome — success or failure, and the same value. Number type and interpreter options are shared
    /// (<see cref="ExpressionCompiler"/> builds every signature with <c>InterpreterOptions.Default</c>).
    /// </summary>
    [Theory]
    [InlineData("context.Body[\"amount\"].AsDouble() > 100000")]
    [InlineData("context.Body[\"amount\"].AsInt32() == 150000")]
    [InlineData("context.Body[\"status\"] == \"x\"")]
    [InlineData("context.Body[\"status\"].ToString() == \"x\"")]
    [InlineData("context.Instance.CurrentState == null")]
    [InlineData("context.Instance.Key == \"ctx-key\"")]
    [InlineData("context.Instance.Data[\"amount\"].AsDouble() > 1000")]
    [InlineData("context.Body[\"missing\"] == null")]
    [InlineData("context.Nope == 1")]
    [InlineData("1 / 2 == 0")]
    [InlineData("1.5 > 1")]
    public async Task Condition_LegacyText_SameOutcomeAsLegacySignature(string code)
    {
        var context = Context();
        Result<bool> legacy;
        try
        {
            var compiled = new ExpressionCompiler(8).Compile<Func<ExpressoRuleContext, bool>>(code, ExpressionSignatures.LegacyCondition);
            legacy = compiled.IsSuccess
                ? Result<bool>.Ok(compiled.Value!.Invoke(ExpressoRuleContextMapper.FromScriptContext(context)))
                : Result<bool>.Fail(compiled.Error);
        }
        catch (Exception ex)
        {
            legacy = Result<bool>.Fail(Error.Failure("legacy", ex.Message));
        }

        var current = await Condition().EvaluateAsync(Expr(code), context);

        current.IsSuccess.ShouldBe(legacy.IsSuccess, current.IsSuccess ? (legacy.IsSuccess ? null : legacy.Error.Message) : current.Error.Message);
        if (legacy.IsSuccess)
            current.Value.ShouldBe(legacy.Value);
    }

    [Theory]
    [InlineData("\"k:\" + context.Instance.Key", "k:ctx-key")]
    [InlineData("sha256(context.Instance.Key)", null)]
    public async Task Key_LegacyContextExpression_EvaluatesAsBefore(string code, string? expected)
    {
        var result = await Key().EvaluateAsync(Expr(code), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        if (expected is null)
            result.Value!.Length.ShouldBe(64); // sha256 hex
        else
            result.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task Key_VaryKeyOfContext_StillAvailable()
    {
        var result = await Key().EvaluateAsync(Expr("sha256(varyKey(context))"), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.Length.ShouldBe(64);
    }

    [Fact]
    public void NewSignatures_UseTheLegacyFunctions_AndDisableAssignment()
    {
        ExpressionSignatures.Condition.Assignment.ShouldBe(DynamicExpresso.AssignmentOperators.None);
        ExpressionSignatures.Key.Assignment.ShouldBe(DynamicExpresso.AssignmentOperators.None);
        ExpressionSignatures.Key.Functions.Keys.ShouldBe(ExpressionSignatures.LegacyValue.Functions.Keys, ignoreOrder: true);
        ExpressionSignatures.Condition.Name.ShouldNotBe(ExpressionSignatures.LegacyCondition.Name);
        ExpressionSignatures.Key.Name.ShouldNotBe(ExpressionSignatures.LegacyValue.Name);
    }

    [Fact]
    public async Task Condition_Assignment_FailsAndDoesNotSelect()
    {
        var result = await Condition().EvaluateAsync(Expr("(context.Instance.Key = \"y\") == \"y\""), Context());

        result.IsSuccess.ShouldBeFalse();
    }

    // ----- new variables: condition ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("data.amount.AsDouble() > 1000", true)]
    [InlineData("data.amount.AsDouble() > 2000", false)]
    [InlineData("data.customer.id.ToString() == \"c-42\"", true)]
    [InlineData("data.flag.AsBoolean()", true)]
    [InlineData("instance.key == \"ctx-key\"", true)]
    [InlineData("instance.flow == \"test-flow\"", true)]
    [InlineData("now.Year == 2026", true)]
    [InlineData("user.available == false", true)]
    [InlineData("input == null", true)]
    public async Task Condition_NewVariables_Evaluate(string code, bool expected)
    {
        var result = await Condition().EvaluateAsync(Expr(code), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task Condition_Input_IsTheTransitionRequestBody()
    {
        var input = JsonDocument.Parse("""{"score":7}""").RootElement;

        var result = await Condition().EvaluateAsync(Expr("input.score.AsInt32() == 7"), Context(transitionInput: input));

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value.ShouldBeTrue();
    }

    [Fact]
    public async Task Condition_CamelCaseInstanceView_PascalCaseIsCompileError()
    {
        var result = await Condition().EvaluateAsync(Expr("instance.CurrentState == \"x\""), Context());

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Condition_UserRoles_WithCaller_Evaluate()
    {
        var context = Context(caller: Caller("approver"));

        var has = await Condition().EvaluateAsync(Expr("user.hasRole(\"approver\")"), context);
        var hasNot = await Condition().EvaluateAsync(Expr("user.hasRole(\"admin\")"), context);

        has.Value.ShouldBeTrue();
        hasNot.IsSuccess.ShouldBeTrue();
        hasNot.Value.ShouldBeFalse();
    }

    [Fact]
    public async Task Condition_MissingValue_ComparisonIsFalse_NotAnError()
    {
        var result = await Condition().EvaluateAsync(Expr("data.customer?.missing == \"x\""), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value.ShouldBeFalse();
    }

    [Fact]
    public async Task Condition_MissingNumber_FailsAndDoesNotSelect()
    {
        var result = await Condition().EvaluateAsync(Expr("data.nope.AsDouble() > 1"), Context());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TransitionRuleFailed);
    }

    [Fact]
    public async Task Condition_MethodCallOnAJsonValue_FailsAndDoesNotSelect()
    {
        // A CLR method on a JSON value is not called: it fails like any throwing rule, with a message that says why.
        var input = JsonDocument.Parse("""{"name":"Ada"}""").RootElement;

        var result = await Condition().EvaluateAsync(Expr("input.name.ToUpper() == \"ADA\""), Context(transitionInput: input));
        var converted = await Condition().EvaluateAsync(Expr("input.name.ToString().ToUpper() == \"ADA\""),
            Context(transitionInput: input));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TransitionRuleFailed);
        result.Error.Message.ShouldContain("convert it first");
        converted.IsSuccess.ShouldBeTrue(converted.IsSuccess ? null : converted.Error.Message);
        converted.Value.ShouldBeTrue();
    }

    [Fact]
    public async Task Key_MethodCallOnAJsonValue_Fails()
    {
        var result = await Key().EvaluateAsync(Expr("data.customer.id.ToUpper()"), Context());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain("convert it first");
    }

    [Fact]
    public async Task Condition_UserRoles_OnHopWithoutCaller_FailsAndDoesNotSelect()
    {
        var result = await Condition().EvaluateAsync(Expr("user.hasRole(\"approver\")"), Context());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TransitionRuleFailed);
    }

    [Fact]
    public async Task Condition_ProviderFailure_Is110006_AndMarksTheAccessorFailed()
    {
        var caller = FailingCaller();

        var result = await Condition().EvaluateAsync(Expr("user.hasRole(\"approver\")"), Context(caller: caller));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrors.CallerRolesUnavailable().Code);
        caller.ResolutionFailed.ShouldBeTrue(); // what every selection site checks to fail closed
    }

    [Fact]
    public async Task Condition_LazyBinding_ContextOnlyRule_NeverResolvesRoles()
    {
        var caller = Caller("approver");

        var result = await Condition().EvaluateAsync(Expr("context.Body[\"amount\"].AsDouble() > 100000"), Context(caller: caller));

        result.Value.ShouldBeTrue();
        caller.ResolutionStarted.ShouldBeFalse();
    }

    // ----- new variables: key ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\"cust:\" + data.customer.id", "cust:c-42")]
    [InlineData("data.customer.id.ToString()", "c-42")]
    [InlineData("\"i:\" + instance.key", "i:ctx-key")]
    [InlineData("\"d:\" + now.ToString(\"yyyyMMdd\")", "d:20261125")]
    [InlineData("\"u:\" + user.available", "u:False")]
    public async Task Key_NewVariables_Evaluate(string code, string expected)
    {
        var result = await Key().EvaluateAsync(Expr(code), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task Key_Sha256OfData_Works()
    {
        var result = await Key().EvaluateAsync(Expr("sha256(data.customer.id.ToString())"), Context());

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value.ShouldBe(DynamicExpressoValueEvaluator.Sha256Hex("c-42"));
    }

    [Fact]
    public async Task Key_Input_IsTheTransitionRequestBody()
    {
        var input = JsonDocument.Parse("""{"orderId":"o-1"}""").RootElement;

        var result = await Key().EvaluateAsync(Expr("\"lock:\" + input.orderId"), Context(transitionInput: input));

        result.Value.ShouldBe("lock:o-1");
    }

    [Fact]
    public async Task Key_UserRoles_OnHopWithoutCaller_Fails()
    {
        var result = await Key().EvaluateAsync(Expr("string.Join(\",\", user.roles)"), Context());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TaskExecution);
    }

    [Fact]
    public async Task Key_ProviderFailure_Is110006()
    {
        var result = await Key().EvaluateAsync(Expr("string.Join(\",\", user.roles)"), Context(caller: FailingCaller()));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrors.CallerRolesUnavailable().Code);
    }

    [Fact]
    public async Task Key_LazyBinding_ContextOnlyKey_NeverResolvesRoles()
    {
        var caller = Caller("approver");

        var result = await Key().EvaluateAsync(Expr("\"k:\" + context.Instance.Key"), Context(caller: caller));

        result.Value.ShouldBe("k:ctx-key");
        caller.ResolutionStarted.ShouldBeFalse();
    }
}
