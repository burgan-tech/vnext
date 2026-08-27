using System;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Runtime;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Unit tests for ComponentValidatorProcessor
/// </summary>
public class ComponentValidatorProcessorTests
{
    [Fact]
    public void Validate_ShouldUseCorrectValidator_ForComponentType()
    {
        // Arrange
        var mockValidator1 = new Mock<IComponentValidator>();
        var mockValidator2 = new Mock<IComponentValidator>();
        
        mockValidator1.Setup(v => v.CanHandle("sys-flows")).Returns(true);
        mockValidator1.Setup(v => v.Validate(It.IsAny<JsonElement>()))
            .Returns(ComponentValidationResult.Success());
        
        mockValidator2.Setup(v => v.CanHandle("sys-tasks")).Returns(true);
        
        var processor = new ComponentValidatorProcessor(new[] { mockValidator1.Object, mockValidator2.Object });
        var attributes = JsonDocument.Parse("{}").RootElement;
        
        // Act
        var result = processor.Validate("sys-flows", attributes);
        
        // Assert
        result.IsValid.ShouldBeTrue();
        mockValidator1.Verify(v => v.Validate(It.IsAny<JsonElement>()), Times.Once);
        mockValidator2.Verify(v => v.Validate(It.IsAny<JsonElement>()), Times.Never);
    }

    [Fact]
    public void Validate_ShouldThrowNotSupportedException_WhenNoValidatorFound()
    {
        // Arrange
        var mockValidator = new Mock<IComponentValidator>();
        mockValidator.Setup(v => v.CanHandle(It.IsAny<string>())).Returns(false);
        
        var processor = new ComponentValidatorProcessor(new[] { mockValidator.Object });
        var attributes = JsonDocument.Parse("{}").RootElement;
        
        // Act & Assert
        var exception = Should.Throw<NotSupportedException>(() => processor.Validate("unknown-type", attributes));
        exception.Message.ShouldContain("unknown-type");
    }

    [Fact]
    public void TryValidate_ShouldReturnFalse_WhenNoValidatorFound()
    {
        // Arrange
        var mockValidator = new Mock<IComponentValidator>();
        mockValidator.Setup(v => v.CanHandle(It.IsAny<string>())).Returns(false);
        
        var processor = new ComponentValidatorProcessor(new[] { mockValidator.Object });
        var attributes = JsonDocument.Parse("{}").RootElement;
        
        // Act
        var found = processor.TryValidate("unknown-type", attributes, out var result);
        
        // Assert
        found.ShouldBeFalse();
        result.IsValid.ShouldBeTrue(); // Returns success by default when no validator found
    }

    [Fact]
    public void TryValidate_ShouldReturnTrue_WhenValidatorFound()
    {
        // Arrange
        var mockValidator = new Mock<IComponentValidator>();
        mockValidator.Setup(v => v.CanHandle("sys-flows")).Returns(true);
        mockValidator.Setup(v => v.Validate(It.IsAny<JsonElement>()))
            .Returns(ComponentValidationResult.Success());
        
        var processor = new ComponentValidatorProcessor(new[] { mockValidator.Object });
        var attributes = JsonDocument.Parse("{}").RootElement;
        
        // Act
        var found = processor.TryValidate("sys-flows", attributes, out var result);
        
        // Assert
        found.ShouldBeTrue();
        result.IsValid.ShouldBeTrue();
    }

    /// <summary>
    /// A definition whose own <c>Configure</c> rejects the authored shape must publish as a
    /// validation failure, not as an opaque HTTP 500 with the exception's message thrown away.
    /// </summary>
    [Fact]
    public void Validate_ShouldReturnValidationError_WhenMaterialisationRejectsTheAuthoredShape()
    {
        // A real validator over a real definition: the reserved FanOutTask mode 'durable'. The whole
        // class of Configure-time authoring errors travels this one path — itemsPath not '$.'-rooted,
        // maxDegreeOfParallelism below 1, HttpTask without a url, SubProcessTask without a domain.
        var processor = new ComponentValidatorProcessor(new[] { (IComponentValidator)new TaskComponentValidator() });
        var attributes = JsonDocument.Parse(
            """
            {
                "type": "21",
                "config": {
                    "mode": "durable",
                    "itemsPath": "$.documents",
                    "task": { "key": "process-document", "domain": "core", "flow": "sys-tasks", "version": "1.0.0" }
                }
            }
            """).RootElement;

        var result = processor.Validate(RuntimeSysSchemaInfo.Tasks, attributes);

        result.IsValid.ShouldBeFalse();
        var error = result.ValidationErrors.ShouldHaveSingleItem();

        // The message an author needs is the one Configure already wrote: it names both the
        // offending mode and the supported one.
        error.ErrorMessage.ShouldContain("durable");
        error.ErrorMessage.ShouldContain("inline");

        // Keyed so the errors dictionary locates the mistake rather than reporting a bare message.
        error.MemberNames.ShouldContain($"{RuntimeSysSchemaInfo.Tasks}.config");
    }

    [Fact]
    public void TryValidate_ShouldReturnValidationError_WhenMaterialisationRejectsTheAuthoredShape()
    {
        // TryValidate is a separate entry point and must not be the one that still throws.
        var mockValidator = new Mock<IComponentValidator>();
        mockValidator.Setup(v => v.CanHandle("sys-tasks")).Returns(true);
        mockValidator.Setup(v => v.Validate(It.IsAny<JsonElement>()))
            .Throws(new ArgumentException("bad shape", "config"));

        var processor = new ComponentValidatorProcessor(new[] { mockValidator.Object });

        var found = processor.TryValidate("sys-tasks", JsonDocument.Parse("{}").RootElement, out var result);

        found.ShouldBeTrue();
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldHaveSingleItem().MemberNames.ShouldContain("sys-tasks.config");
    }

    /// <summary>
    /// The counterpart guard: only authoring errors become validation failures. A genuine fault must
    /// still escape and be reported as a server error — turning real faults into 400s would be worse
    /// than the bug this catch fixes.
    /// </summary>
    [Fact]
    public void Validate_ShouldNotSwallow_ExceptionsThatAreNotAuthoringErrors()
    {
        var mockValidator = new Mock<IComponentValidator>();
        mockValidator.Setup(v => v.CanHandle("sys-tasks")).Returns(true);
        mockValidator.Setup(v => v.Validate(It.IsAny<JsonElement>()))
            .Throws(new InvalidOperationException("the component store is unreachable"));

        var processor = new ComponentValidatorProcessor(new[] { mockValidator.Object });

        Should.Throw<InvalidOperationException>(
            () => processor.Validate("sys-tasks", JsonDocument.Parse("{}").RootElement));
    }
}
