using Xunit;

namespace BBT.Workflow.Domain.Tests.Runtime;

/// <summary>
/// Tests that write process-wide environment variables (<c>APP_DOMAIN</c>, <c>APP_VERSION</c>).
/// The environment is shared by every test in the process, and <c>WorkflowTestBase.AddApplication</c>
/// sets <c>APP_DOMAIN=core</c> whenever a test builds an application — so a test that clears
/// <c>APP_DOMAIN</c> and then reads it raced those tests and failed now and then
/// (<c>Constructor_WhenAppDomainNotSet_ShouldThrow</c>). <c>DisableParallelization</c> runs this
/// collection on its own, after the parallel ones.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}
