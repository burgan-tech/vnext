using Xunit;

namespace BBT.Workflow.Application.Tests;

/// <summary>
/// Serializes every test that reconfigures <c>AetherTracingRuntime.DetailLevel</c>.
/// </summary>
/// <remarks>
/// The detail level is PROCESS-GLOBAL, and xUnit runs test classes in parallel — so a class that
/// switches it to Verbose can silently break a concurrently running class that asserts spans are
/// NOT created in Business mode, and vice versa. Neither test is wrong; they simply cannot share a
/// process at the same instant. Membership of this collection is the only thing keeping them apart,
/// so any new test that calls <c>AetherTracingRuntime.Configure</c> must join it.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class TracingDetailLevelCollection
{
    public const string Name = "TracingDetailLevel";
}
