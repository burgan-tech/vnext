using BBT.Workflow.Instances;

namespace BBT.Workflow.Domains.Instances;

[Xunit.Collection(InfrastructureEntryPointCollection.Name)]
public class EfCoreInstanceRepositoryTests : InstanceRepositoryTests<InfrastructureEntryPoint>;