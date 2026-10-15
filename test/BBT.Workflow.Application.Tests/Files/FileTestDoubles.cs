using System.Threading;
using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Files;
using NSubstitute;

namespace BBT.Workflow.Application.Files;

/// <summary>Pass-through x-storage doubles for suites that do not exercise the file swap.</summary>
internal static class FileTestDoubles
{
    /// <summary>An offloader that reports every payload unchanged.</summary>
    public static IFileOffloadService PassThroughOffload()
    {
        var offload = Substitute.For<IFileOffloadService>();
        offload.OffloadAsync(Arg.Any<FileOffloadRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Result<FileOffloadResult>.Ok(
                new FileOffloadResult(ci.Arg<FileOffloadRequest>().Payload, Changed: false)));
        return offload;
    }

    /// <summary>An admission that always succeeds without touching the request.</summary>
    public static IFileAdmission PassThroughAdmission()
    {
        var admission = Substitute.For<IFileAdmission>();
        admission.ApplyAsync(Arg.Any<TransitionExecutionContext>(), Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        return admission;
    }
}
