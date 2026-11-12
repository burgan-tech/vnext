using BBT.Workflow.Instances;

namespace BBT.Workflow.Applications.Instances;

[Xunit.Collection(InfrastructureEntryPointCollection.Name)]
public class EfCoreInstanceCommandAppServiceTests : InstanceCommandAppServiceTests<InfrastructureEntryPoint>;