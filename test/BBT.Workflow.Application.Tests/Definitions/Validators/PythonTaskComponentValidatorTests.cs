using System.Text.Json;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

public sealed class PythonTaskComponentValidatorTests
{
    private readonly TaskComponentValidator _validator = new();

    [Theory]
    [InlineData("credit_score")]
    [InlineData("models.risk-v2")]
    [InlineData("_private")]
    public void Validate_AcceptsHandlerReference(string handler)
    {
        var result = Validate($$$"""
            {"type":"23","config":{"handler":"{{{handler}}}","input":{"value":1},"timeoutSeconds":30}}
            """);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_RequiresHandler()
    {
        var result = Validate("""{"type":"23","config":{"input":{"value":1}}}""");

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(error => error.ErrorMessage!.Contains("requires a 'handler'"));
    }

    [Theory]
    [InlineData("1starts-with-digit")]
    [InlineData("has space")]
    [InlineData("path/like")]
    public void Validate_RejectsMalformedHandler(string handler)
    {
        var result = Validate($$$"""{"type":"23","config":{"handler":"{{{handler}}}"}}""");

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(error => error.ErrorMessage!.Contains("handler must start"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Validate_RejectsTimeoutOutsideTheContractLimit(int timeout)
    {
        var result = Validate($$$"""{"type":"23","config":{"handler":"h","timeoutSeconds":{{{timeout}}}}}""");

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(error => error.ErrorMessage!.Contains("timeoutSeconds"));
    }

    [Fact]
    public void Validate_RejectsOversizedInput()
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "23",
            config = new { handler = "h", input = new string('x', PythonTask.MaxInputBytes) }
        });

        Validate(json).ValidationErrors.ShouldContain(error => error.ErrorMessage!.Contains("input exceeds"));
    }

    private ComponentValidationResult Validate(string json) =>
        _validator.Validate(JsonDocument.Parse(json).RootElement);
}
