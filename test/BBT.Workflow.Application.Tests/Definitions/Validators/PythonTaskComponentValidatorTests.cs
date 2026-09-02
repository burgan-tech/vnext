using System;
using System.Text.Json;
using BBT.Workflow.Definitions.Validators;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

public sealed class PythonTaskComponentValidatorTests
{
    private readonly TaskComponentValidator _validator = new();

    [Fact]
    public void Validate_AcceptsNativeInlinePythonTask()
    {
        var result = Validate("""
            {
              "type":"23",
              "config":{
                "script":{"location":"model.py","code":"def main(input): return input","encoding":"NAT"},
                "executionMode":"pythonNet",
                "input":{"value":1},
                "timeoutSeconds":30
              }
            }
            """);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("unknown", 30, "executionMode")]
    [InlineData("process", 0, "timeoutSeconds")]
    [InlineData("container", 51, "timeoutSeconds")]
    public void Validate_RejectsInvalidModeOrTimeout(string mode, int timeout, string message)
    {
        var result = Validate($$"""
            {
              "type":"23",
              "config":{
                "script":{"code":"def main(input): return input","encoding":"NAT"},
                "executionMode":"{{mode}}",
                "timeoutSeconds":{{timeout}}
              }
            }
            """);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(error => error.ErrorMessage!.Contains(message));
    }

    [Fact]
    public void Validate_RejectsReferenceScript()
    {
        var result = Validate("""
            {
              "type":"23",
              "config":{
                "script":{
                  "encoding":"REF",
                  "code":{"key":"shared","domain":"d","flow":"sys-mappings","version":"1.0.0"}
                }
              }
            }
            """);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(error => error.ErrorMessage!.Contains("REF encoding"));
    }

    [Fact]
    public void Validate_ReportsInvalidBase64WithoutThrowing()
    {
        var result = Validate("""
            {"type":"23","config":{"script":{"code":"not-base64","encoding":"B64"}}}
            """);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(error => error.ErrorMessage!.Contains("not valid Base64"));
    }

    [Fact]
    public void Validate_RejectsFilesystemLocation()
    {
        var result = Validate("""
            {
              "type":"23",
              "config":{
                "script":{
                  "location":"/opt/workflow/task.py",
                  "code":"def main(input): return input",
                  "encoding":"NAT"
                }
              }
            }
            """);

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(error => error.ErrorMessage!.Contains("filesystem paths"));
    }

    [Fact]
    public void Validate_RejectsOversizedCodeAndInput()
    {
        var oversizedCode = JsonSerializer.Serialize(new
        {
            type = "22",
            config = new
            {
                script = new
                {
                    code = new string('x', PythonTask.MaxCodeBytes + 1),
                    encoding = "NAT"
                }
            }
        });
        var oversizedInput = JsonSerializer.Serialize(new
        {
            type = "22",
            config = new
            {
                script = new { code = "def main(input): return input", encoding = "NAT" },
                input = new string('x', PythonTask.MaxInputBytes)
            }
        });

        var codeResult = Validate(oversizedCode);
        var inputResult = Validate(oversizedInput);

        codeResult.ValidationErrors.ShouldContain(error => error.ErrorMessage!.Contains("script exceeds"));
        inputResult.ValidationErrors.ShouldContain(error => error.ErrorMessage!.Contains("input exceeds"));
    }

    private ComponentValidationResult Validate(string json) =>
        _validator.Validate(JsonDocument.Parse(json).RootElement);
}
