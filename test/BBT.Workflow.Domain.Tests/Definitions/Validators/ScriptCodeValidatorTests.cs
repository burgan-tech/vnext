using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Runtime;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions.Validators;

/// <summary>
/// Unit tests for <see cref="ScriptCodeValidator"/>. The cases mirror the vnext-schema guard so the
/// runtime validator and the authoring schema agree on what a publishable script slot looks like.
/// </summary>
public class ScriptCodeValidatorTests
{
    private const string Member = "Workflow.Transitions[x].Mapping";

    private static ScriptCode Parse(string json) =>
        JsonSerializer.Deserialize<ScriptCode>(json, JsonSerializerConstants.JsonOptions)!;

    private static string Base64(string code) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(code));

    private static List<ValidationResult> Validate(ScriptCode? script)
    {
        var errors = new List<ValidationResult>();
        ScriptCodeValidator.Validate(script, Member, errors);
        return errors;
    }

    [Fact]
    public void Validate_ShouldPass_WhenScriptIsNull()
    {
        Validate(null).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ShouldFail_WhenOnlyLocationIsDeclared()
    {
        // The exact shape produced when the domain build step never inlined the .csx body.
        var errors = Validate(Parse("""{"location":"./src/NsNotifySpawnMapping.csx"}"""));

        errors.ShouldHaveSingleItem();
        errors[0].MemberNames.ShouldContain(Member);
        errors[0].ErrorMessage!.ShouldContain("./src/NsNotifySpawnMapping.csx");
        errors[0].ErrorMessage!.ShouldContain("'code'");
    }

    [Fact]
    public void Validate_ShouldFail_WhenLocalTypeDeclaresNoCode()
    {
        Validate(Parse("""{"type":"L","location":"./x.csx"}""")).ShouldHaveSingleItem();
    }

    [Fact]
    public void Validate_ShouldPass_WhenGlobalTypeDeclaresNoCode()
    {
        // Global carries no body by design - HasMappingCode is false and the runtime never compiles it.
        Validate(Parse("""{"type":"G","location":"./x.csx"}""")).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ShouldPass_ForValidBase64Code()
    {
        var json = $$"""{"location":"./x.csx","code":"{{Base64("return true;")}}","encoding":"B64"}""";

        Validate(Parse(json)).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ShouldPass_ForNativeCode()
    {
        Validate(Parse("""{"location":"./x.csx","code":"return true;","encoding":"NAT"}""")).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ShouldFail_WhenCodeIsWhitespace()
    {
        Validate(Parse("""{"location":"./x.csx","code":"   ","encoding":"NAT"}""")).ShouldHaveSingleItem();
    }

    [Fact]
    public void Validate_ShouldFail_WhenBase64DecodesToWhitespace()
    {
        var json = $$"""{"location":"./x.csx","code":"{{Base64("  \n ")}}","encoding":"B64"}""";

        var errors = Validate(Parse(json));

        errors.ShouldHaveSingleItem();
        errors[0].ErrorMessage!.ShouldContain("empty");
    }

    [Fact]
    public void Validate_ShouldFail_WhenBase64IsNotDecodable()
    {
        var errors = Validate(Parse("""{"location":"./x.csx","code":"not-base64!","encoding":"B64"}"""));

        errors.ShouldHaveSingleItem();
        errors[0].ErrorMessage!.ShouldContain("Base64");
    }

    [Fact]
    public void Validate_ShouldFail_WhenReferenceEncodingHasNoReference()
    {
        // encoding REF with a string body: the converter drops the string, leaving nothing to resolve.
        var errors = Validate(Parse("""{"location":"./x.csx","code":"abc","encoding":"REF"}"""));

        errors.ShouldHaveSingleItem();
        errors[0].ErrorMessage!.ShouldContain("REF");
    }

    [Fact]
    public void Validate_ShouldPass_ForCompleteReference()
    {
        var json = $$"""
        {
            "location": "./x.csx",
            "encoding": "REF",
            "code": {
                "key": "json-helper",
                "domain": "core",
                "flow": "{{RuntimeSysSchemaInfo.Mappings}}",
                "version": "1.0.0"
            }
        }
        """;

        Validate(Parse(json)).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ShouldFail_WhenReferenceIsMissingVersion()
    {
        var json = $$"""
        {
            "encoding": "REF",
            "code": { "key": "json-helper", "domain": "core", "flow": "{{RuntimeSysSchemaInfo.Mappings}}" }
        }
        """;

        Validate(Parse(json)).ShouldHaveSingleItem();
    }

    [Fact]
    public void Validate_ShouldFail_WhenReferencePointsAtAnotherFlow()
    {
        var json = """
        {
            "encoding": "REF",
            "code": { "key": "some-task", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" }
        }
        """;

        var errors = Validate(Parse(json));

        errors.ShouldHaveSingleItem();
        errors[0].ErrorMessage!.ShouldContain(RuntimeSysSchemaInfo.Mappings);
    }

    [Fact]
    public void Validate_ShouldPass_WhenGlobalTypeUsesReferenceEncodingWithoutReference()
    {
        // Global short-circuits before any body check, whatever the declared encoding.
        Validate(Parse("""{"type":"G","encoding":"REF"}""")).ShouldBeEmpty();
    }

    [Fact]
    public void ValidateMany_ShouldReportEveryBrokenSlot()
    {
        var errors = new List<ValidationResult>();

        ScriptCodeValidator.Validate(Parse("""{"location":"./a.csx"}"""), "A", errors);
        ScriptCodeValidator.Validate(Parse("""{"location":"./b.csx"}"""), "B", errors);

        errors.Count.ShouldBe(2);
        errors.Select(e => e.MemberNames.Single()).ShouldBe(new[] { "A", "B" });
    }
}
