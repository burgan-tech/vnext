using System;
using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Expressions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

public class MappingBlockValidatorTests
{
    private const string TaskRef = """{"key":"t","domain":"d","flow":"sys-tasks","version":"1.0.0"}""";
    private const string Loc = "states[review].onEntries[0]";

    private readonly MappingBlockValidator _sut = new(new ExpressionCompiler(64));

    private static OnExecuteTask Binding(string extra) =>
        JsonSerializer.Deserialize<OnExecuteTask>(
            "{\"order\":1,\"task\":" + TaskRef + (extra.Length == 0 ? "" : "," + extra) + "}",
            JsonSerializerConstants.JsonOptions)!;

    private List<string> Errors(string extra, string location = Loc)
    {
        var errors = new List<ValidationResult>();
        _sut.ValidateBinding(Binding(extra), location, errors);
        return errors.Select(e => e.ErrorMessage!).ToList();
    }

    [Fact]
    public void Valid_DeclarativeBinding_NoErrors()
    {
        Errors("""
            "input":{"select":"$.data.customer","into":"$.body","set":{"$.headers.x-channel":"user.available","$.url":"task.url.ToString().Replace(\"API_BASEURL\", config.get(\"Api:BaseUrl\", \"http://x\")) + \"/\" + urlEncode(data.id)"}},
            "output":{"select":"$.body.score","into":"$.kyc.score","set":{"$.kyc.ok":"result.statusCode.AsInt32() == 200"}}
            """).ShouldBeEmpty();
    }

    [Fact]
    public void Binding_WithNeitherMappingNorBlocks_StaysValid() => Errors("").ShouldBeEmpty();

    [Theory]
    [InlineData("data.x")]
    [InlineData("")]
    public void D1_DynamicExpressoMapping_IsAWarning_NotAnError(string text)
    {
        // The script engine never read ScriptCode.Location: such a mapping has always run as a C# script (an empty one as
        // a no-op). Rejecting it would break published components, so publish only warns.
        var code = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var errors = new List<ValidationResult>();
        var warnings = new List<ValidationResult>();

        _sut.ValidateBinding(Binding($$"""
            "mapping":{"location":"dynamicExpresso","code":"{{code}}"}
            """), Loc, errors, warnings);

        errors.ShouldBeEmpty();
        var warning = warnings.ShouldHaveSingleItem().ErrorMessage!;
        warning.ShouldStartWith($"{Loc}.mapping: ");
        warning.ShouldContain("ignored");
        warning.ShouldContain("'input'");
        warning.ShouldContain("'output'");
    }

    [Fact]
    public void D1_Warning_ReachesTheComponentResult_AndKeepsItValid()
    {
        var code = Convert.ToBase64String(Encoding.UTF8.GetBytes("data.x"));
        var fn = JsonDocument.Parse("""
            {"scope":"F","task":{"order":1,"task":__T__,"mapping":{"location":"dynamicExpresso","code":"__C__"}}}
            """.Replace("__T__", TaskRef).Replace("__C__", code)).RootElement;
        var result = new ComponentValidationResult();

        _sut.Validate(RuntimeSysSchemaInfo.Functions, fn, result);

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldHaveSingleItem().ErrorMessage!.ShouldStartWith("task.mapping: ");
    }

    [Theory]
    [InlineData("data.x = 1")]
    [InlineData("input.amount = 2")]
    public void AssignmentInASetExpression_IsRejected(string expression)
    {
        var errors = Errors("""
            "output":{"set":{"$.x":"__E__"}}
            """.Replace("__E__", expression));
        errors.ShouldHaveSingleItem().ShouldStartWith($"{Loc}.output.set['$.x']: The expression does not compile");
    }

    [Fact]
    public void D4_MappingTogetherWithBlocks_IsRejected()
    {
        var code = Convert.ToBase64String(Encoding.UTF8.GetBytes("// noop"));
        var errors = Errors($$"""
            "mapping":{"location":"./a.csx","code":"{{code}}"},
            "output":{"into":"$.a"}
            """);
        errors.ShouldContain(e => e.StartsWith($"{Loc}: ") && e.Contains("mapping"));
    }

    [Fact]
    public void D5_IndexedInto_IsRejected()
    {
        var errors = Errors("""
            "output":{"select":"$.body","into":"$.items[0]"}
            """);
        errors.ShouldHaveSingleItem().ShouldStartWith($"{Loc}.output.into: ");
    }

    [Fact]
    public void D5_IndexedSetKey_IsRejected()
    {
        var errors = Errors("""
            "output":{"set":{"$.items[*].a":"1"}}
            """);
        errors.ShouldHaveSingleItem().ShouldStartWith($"{Loc}.output.set['$.items[*].a']: ");
    }

    [Fact]
    public void D6_FilterPath_IsRejected()
    {
        var errors = Errors("""
            "output":{"select":"$.body.items[?(@.x==1)]","into":"$.a"}
            """);
        errors.ShouldHaveSingleItem().ShouldStartWith($"{Loc}.output.select: ");
    }

    [Fact]
    public void ExpressionParseError_IsReported_WithLocation()
    {
        var errors = Errors("""
            "output":{"set":{"$.kyc.score":"1 +"}}
            """, "states[review].onEntries[0]");
        errors.ShouldHaveSingleItem().ShouldStartWith("states[review].onEntries[0].output.set['$.kyc.score']: ");
    }

    [Fact]
    public void InputExpression_CannotUseResult()
    {
        var errors = Errors("""
            "input":{"set":{"$.body.x":"result.body"}}
            """);
        errors.ShouldHaveSingleItem().ShouldStartWith($"{Loc}.input.set['$.body.x']: ");
    }

    [Fact]
    public void OutputExpression_MayUseResult()
    {
        Errors("""
            "output":{"set":{"$.x":"result.body.id"}}
            """).ShouldBeEmpty();
    }

    [Fact]
    public void ExpressionOverMaxLength_IsRejected()
    {
        var longExpr = "1" + string.Concat(Enumerable.Repeat(" + 1", 3000));
        var errors = Errors("""
            "output":{"set":{"$.x":"__E__"}}
            """.Replace("__E__", longExpr));
        errors.ShouldHaveSingleItem().ShouldContain("8000");
    }

    [Theory]
    [InlineData("$.url")]
    [InlineData("$.data.x")]
    [InlineData("$.queryString")]
    [InlineData("$.route.id")]
    public void InputTarget_IsAnyTaskConfigField(string into)
    {
        // A target addresses the task's config; which fields a task TYPE reads is not resolvable at publish.
        Errors($$$"""
            "input":{"select":"$.data.a","into":"{{{into}}}","set":{"{{{into}}}":"1"}}
            """).ShouldBeEmpty();
    }

    [Fact]
    public void InputTarget_Root_IsRejected()
    {
        var errors = Errors("""
            "input":{"select":"$.data.a","into":"$"}
            """);
        errors.ShouldHaveSingleItem().ShouldStartWith($"{Loc}.input.into: ");
        errors[0].ShouldContain("field of the task's config");
    }

    [Fact]
    public void InputSelect_MayReadTheTaskConfig_ButNotHostConfig()
    {
        Errors("""
            "input":{"select":"$.task.body","into":"$.body.copy"}
            """).ShouldBeEmpty();
        Errors("""
            "input":{"select":"$.config.Api","into":"$.body.copy"}
            """).ShouldHaveSingleItem().ShouldStartWith($"{Loc}.input.select: ");
    }

    [Fact]
    public void InputSelect_MustStartAtAnInputVariable()
    {
        var errors = Errors("""
            "input":{"select":"$.result.body","into":"$.body"}
            """);
        errors.ShouldHaveSingleItem().ShouldStartWith($"{Loc}.input.select: ");
    }

    [Fact]
    public void OutputInto_RootPath_IsRejected()
    {
        var errors = Errors("""
            "output":{"select":"$.body","into":"$"}
            """);
        errors.ShouldHaveSingleItem().ShouldStartWith($"{Loc}.output.into: ");
    }

    [Fact]
    public void Function_And_Extension_Bindings_AreCovered()
    {
        var fn = JsonDocument.Parse("""
            {"scope":"F","task":{"order":1,"task":__T__,"output":{"into":"$.a[0]"}}}
            """.Replace("__T__", TaskRef)).RootElement;
        var r1 = new ComponentValidationResult();
        _sut.Validate(RuntimeSysSchemaInfo.Functions, fn, r1);
        r1.ValidationErrors.ShouldHaveSingleItem().ErrorMessage.ShouldStartWith("task.output.into: ");

        var ext = JsonDocument.Parse("""
            {"type":"global","scope":"everywhere","task":{"order":1,"task":__T__,"output":{"into":"$.a[0]"}}}
            """.Replace("__T__", TaskRef)).RootElement;
        var r2 = new ComponentValidationResult();
        _sut.Validate(RuntimeSysSchemaInfo.Extensions, ext, r2);
        r2.ValidationErrors.ShouldHaveSingleItem().ErrorMessage.ShouldStartWith("task.output.into: ");
    }

    [Fact]
    public void Function_OnExecutionTasks_AreCovered()
    {
        var fn = JsonDocument.Parse("""
            {"scope":"F","onExecutionTasks":[{"order":1,"task":__T__},{"order":2,"task":__T__,"output":{"into":"$.a[0]"}}]}
            """.Replace("__T__", TaskRef)).RootElement;
        var r = new ComponentValidationResult();
        _sut.Validate(RuntimeSysSchemaInfo.Functions, fn, r);
        r.ValidationErrors.ShouldHaveSingleItem().ErrorMessage.ShouldStartWith("onExecutionTasks[1].output.into: ");
    }

    [Fact]
    public void Flow_StateEntries_TransitionTasks_And_WellKnownTransitions_AreCovered()
    {
        string Bad(string name) => "{\"order\":1,\"task\":" + TaskRef + ",\"output\":{\"into\":\"$." + name + "[0]\"}}";
        var flow = JsonDocument.Parse($$"""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                {
                    "key": "review", "stateType": "initial",
                    "labels": [{"label": "Review", "language": "en"}],
                    "onEntries": [{{Bad("entry")}}],
                    "onExits": [{{Bad("exit")}}],
                    "transitions": [
                        { "key": "approve", "target": "done", "triggerType": "manual",
                          "labels": [{"label": "Approve", "language": "en"}],
                          "onExecutionTasks": [{{Bad("tr")}}] }
                    ]
                },
                { "key": "done", "stateType": "finish", "labels": [{"label": "Done", "language": "en"}], "transitions": [] }
            ],
            "startTransition": { "key": "start", "target": "review", "triggerType": "manual",
                "labels": [{"label": "Start", "language": "en"}], "onExecutionTasks": [{{Bad("st")}}] },
            "cancel": { "key": "cancel", "target": "done", "triggerType": "manual",
                "labels": [{"label": "Cancel", "language": "en"}], "onExecutionTasks": [{{Bad("ca")}}] },
            "sharedTransitions": [
                { "key": "note", "target": "$self", "triggerType": "manual", "availableIn": ["review"],
                  "labels": [{"label": "Note", "language": "en"}], "onExecutionTasks": [{{Bad("sh")}}] }
            ]
        }
        """).RootElement;
        var r = new ComponentValidationResult();
        _sut.Validate(RuntimeSysSchemaInfo.Flows, flow, r);
        var msgs = r.ValidationErrors.Select(e => e.ErrorMessage!).ToList();
        msgs.ShouldContain(m => m.StartsWith("states[review].onEntries[0].output.into: "));
        msgs.ShouldContain(m => m.StartsWith("states[review].onExits[0].output.into: "));
        msgs.ShouldContain(m => m.StartsWith("states[review].transitions[approve].onExecutionTasks[0].output.into: "));
        msgs.ShouldContain(m => m.StartsWith("startTransition.onExecutionTasks[0].output.into: "));
        msgs.ShouldContain(m => m.StartsWith("cancel.onExecutionTasks[0].output.into: "));
        msgs.ShouldContain(m => m.StartsWith("sharedTransitions[note].onExecutionTasks[0].output.into: "));
        msgs.Count.ShouldBe(6);
    }

    [Fact]
    public void InputSelect_WithoutInto_IsRejected()
    {
        var errors = Errors("""
            "input":{"select":"$.data.customer"}
            """);
        errors.ShouldHaveSingleItem().ShouldStartWith($"{Loc}.input.select: ");
    }

    [Fact]
    public void InputInto_WithoutSelect_IsRejected()
    {
        var errors = Errors("""
            "input":{"into":"$.body.customer"}
            """);
        errors.ShouldHaveSingleItem().ShouldStartWith($"{Loc}.input.into: ");
    }

    [Fact]
    public void OutputSelect_And_Into_StayOptionalOnTheirOwn()
    {
        Errors("""
            "output":{"select":"$.body.customer"}
            """).ShouldBeEmpty();
        Errors("""
            "output":{"into":"$.response"}
            """).ShouldBeEmpty();
    }
}
