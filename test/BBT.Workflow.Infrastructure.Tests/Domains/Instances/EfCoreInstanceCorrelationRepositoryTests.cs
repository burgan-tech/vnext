using BBT.Workflow.Instances;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Tests for EfCoreInstanceCorrelationRepository implementation using InfrastructureEntryPoint.
/// </summary>
[Xunit.Collection(InfrastructureEntryPointCollection.Name)]
public class EfCoreInstanceCorrelationRepositoryTests : InstanceCorrelationRepositoryTests<InfrastructureEntryPoint>;

