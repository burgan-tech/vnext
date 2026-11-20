using System;
using BBT.Workflow.Execution.ErrorHandling;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.ErrorHandling;

/// <summary>
/// vnext#1268: an exception's execution error keeps the whole rendering — type, message, the inner
/// exception and every frame — because the cause of a wrapped failure is the inner exception.
/// </summary>
public sealed class ExecutionErrorFactoryStackTraceTests
{
    [Fact]
    public void CreateFromException_KeepsTheInnerExceptionInTheStackTrace()
    {
        var factory = new ExecutionErrorFactory(new ErrorNormalizer());
        Exception thrown;
        try
        {
            try
            {
                _ = int.Parse("not-a-number");
                throw new InvalidOperationException("unreachable");
            }
            catch (FormatException inner)
            {
                throw new InvalidOperationException("the input handler could not read the amount", inner);
            }
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        var error = factory.CreateFromException(thrown, "read-amount", "Script", 3);

        error.StackTrace.ShouldBe(thrown.ToString());
        error.StackTrace.ShouldContain(nameof(FormatException));
        error.StackTrace.ShouldContain(nameof(CreateFromException_KeepsTheInnerExceptionInTheStackTrace));
        error.ErrorMessage.ShouldBe("the input handler could not read the amount");
    }
}
