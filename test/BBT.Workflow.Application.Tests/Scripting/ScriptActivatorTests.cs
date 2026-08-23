using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Functions;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Pins <see cref="ScriptActivator"/>'s contract: a fresh instance per call, with
/// <see cref="ScriptBase.SetServices"/> applied when the compiled type derives from
/// <see cref="ScriptBase"/> and services are supplied — the same semantics the evaluator's historical
/// <c>CreateAndInjectServices</c> had via <see cref="System.Activator.CreateInstance(System.Type)"/>.
/// </summary>
public class ScriptActivatorTests
{
    [Fact]
    public void Create_ReturnsFreshInstances_WithServicesInjected()
    {
        var services = Mock.Of<IScriptServices>();
        var i1 = ScriptActivator.Create<ActivatorProbe>(typeof(ActivatorProbe), services);
        var i2 = ScriptActivator.Create<ActivatorProbe>(typeof(ActivatorProbe), services);

        i1.ShouldNotBeSameAs(i2);                    // her çağrı taze instance
        i1.ExposedServices.ShouldBeSameAs(services);  // ScriptBase.SetServices çağrıldı
    }

    public class ActivatorProbe : ScriptBase
    {
        public IScriptServices? ExposedServices => Services;
    }
}
