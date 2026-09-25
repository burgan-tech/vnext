using BBT.Workflow.Functions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Functions;

/// <summary>
/// Regression guard for the producer/consumer wiring of the function-execution journal
/// (vnext-client-sdk-core#60). The whole async design depends on the producer
/// (<see cref="IFunctionExecutionJournal"/>, injected into the scoped <c>FunctionAppService</c>) and the
/// consumer (the singleton <c>FunctionExecutionJournalWriter</c>, which drains the concrete
/// <see cref="FunctionExecutionJournal"/>) sharing the EXACT SAME instance. The production registration
/// achieves that with a concrete singleton plus an interface forward. If that ever regresses to a plain
/// <c>AddSingleton&lt;IFunctionExecutionJournal, FunctionExecutionJournal&gt;()</c> (a second instance)
/// or the concrete registration is dropped, every enqueue would silently go to an instance the writer
/// never reads and 100% of journaling would stop with no error — the same failure class the task-invocation
/// DI test already guards. This asserts it against the real <c>AddApplicationModule()</c> wiring.
/// </summary>
public sealed class FunctionExecutionJournalDiRegistrationTests
{
    [Fact]
    public void Journal_InterfaceAndConcrete_ResolveToTheSameSingleton()
    {
        var services = new ServiceCollection();
        // The journal reads its capacity from IOptions<FunctionExecutionJournalOptions>, whose
        // BindConfiguration needs an IConfiguration present; an empty one yields the defaults.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddApplicationModule();

        using var provider = services.BuildServiceProvider();

        var producerPort = provider.GetRequiredService<IFunctionExecutionJournal>();
        var writerView = provider.GetRequiredService<FunctionExecutionJournal>();

        // Same object: what the producer enqueues into is what the writer drains.
        producerPort.ShouldBeSameAs(writerView);

        // And it is a singleton: a second resolve returns the same instance.
        provider.GetRequiredService<IFunctionExecutionJournal>().ShouldBeSameAs(producerPort);
    }
}
