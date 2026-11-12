using BBT.Workflow.Instances;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Tests for EfCoreInstanceTaskRepository implementation using InfrastructureEntryPoint.
/// </summary>
[Xunit.Collection(InfrastructureEntryPointCollection.Name)]
public class EfCoreInstanceTaskRepositoryTests : InstanceTaskRepositoryTests<InfrastructureEntryPoint>;
