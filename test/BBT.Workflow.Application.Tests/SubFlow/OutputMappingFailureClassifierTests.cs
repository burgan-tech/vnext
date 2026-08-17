using System;
using System.IO;
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
    public void IsTransient_ForCancellation_ShouldBeTrue()
        => OutputMappingFailureClassifier.IsTransient(new OperationCanceledException()).ShouldBeTrue();

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
}
