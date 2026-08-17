using System;
using System.IO;
using System.Reflection;
using BBT.Workflow.SubFlow;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

public sealed class OutputMappingFailureClassifierTests
{
    [Fact]
    public void IsTransient_ForAssemblyLoadFailure_ShouldBeTrue()
        => OutputMappingFailureClassifier.IsTransient(new FileLoadException()).ShouldBeTrue();

    [Fact]
    public void IsTransient_ForBadImageFormat_ShouldBeTrue()
        => OutputMappingFailureClassifier.IsTransient(new BadImageFormatException()).ShouldBeTrue();

    [Fact]
    public void IsTransient_ForCancellation_ShouldBeFalse()
    {
        // A bare OperationCanceledException is not on the allowlist. Genuine "our own cancellation"
        // (shutdown, caller gone) is handled as its own catch in ApplyAsync, ahead of the classifier —
        // it never reaches IsTransient. Anything that does reach here is a downstream fault (e.g. a
        // DaprClient call timing out as TaskCanceledException) and must fault the parent visibly,
        // not be redelivered forever with no maxRetries/dead-letter configured.
        OutputMappingFailureClassifier.IsTransient(new OperationCanceledException()).ShouldBeFalse();
    }

    [Fact]
    public void IsTransient_ForWrappedAssemblyLoadFailure_ShouldBeTrue()
        => OutputMappingFailureClassifier
            .IsTransient(new InvalidOperationException("outer", new FileLoadException()))
            .ShouldBeTrue();

    [Fact]
    public void IsTransient_ForACompilationError_ShouldBeFalse()
        => OutputMappingFailureClassifier
            .IsTransient(new InvalidOperationException("Compilation failed:\nCS1002"))
            .ShouldBeFalse();

    [Fact]
    public void IsTransient_ForAnUnclassifiedException_ShouldBeFalse()
        => OutputMappingFailureClassifier.IsTransient(new NotSupportedException()).ShouldBeFalse();

    [Fact]
    public void IsTransient_ForReflectionTypeLoadExceptionWrappingAssemblyLoadFailure_ShouldBeTrue()
        => OutputMappingFailureClassifier
            .IsTransient(new ReflectionTypeLoadException(
                classes: new Type?[] { null },
                exceptions: new Exception?[] { new FileLoadException() }))
            .ShouldBeTrue();

    [Fact]
    public void IsTransient_ForReflectionTypeLoadExceptionWrappingOnlyPermanentFailures_ShouldBeFalse()
        => OutputMappingFailureClassifier
            .IsTransient(new ReflectionTypeLoadException(
                classes: new Type?[] { null },
                exceptions: new Exception?[] { new NotSupportedException() }))
            .ShouldBeFalse();
}
