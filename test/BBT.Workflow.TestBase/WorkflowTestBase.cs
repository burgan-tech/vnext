using System;
using BBT.Aether.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BBT.Workflow;

/// <summary>
/// Base class for all test classes in the Workflow project.
/// Provides common setup and utility methods for tests.
/// </summary>
/// <typeparam name="TEntry">The entry point type for the module being tested.</typeparam>
public abstract class WorkflowTestBase<TEntry> : TestBase<TEntry>
    where TEntry : ModuleEntryPointBase, new()
{
    /// <summary>
    /// Pre-registers an empty in-memory <see cref="IConfiguration"/> so that <c>AddAetherCore</c>, which the Aether
    /// <c>TestBase</c> constructor calls first, skips <c>ConfigurationHelper.BuildConfiguration</c>. That helper builds a
    /// file-backed root over the working directory with <c>reloadOnChange: true</c>; the test base replaces it with an
    /// in-memory configuration right after, but the root and its <c>PhysicalFilesWatcher</c> are never disposed. One leaked
    /// watcher per test (thousands per run) floods the thread pool with change-token callbacks on file-system events and
    /// starved <c>InstanceTests.LatestData_ShouldReturnCorrectResult_DuringConcurrentAccess</c> into a hang. No test
    /// project ships an <c>appsettings*.json</c>, so the skipped root contributed nothing the tests read.
    /// </summary>
    protected override IServiceCollection CreateServiceCollection()
    {
        var services = base.CreateServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        return services;
    }

    /// <summary>
    /// Adds application-specific services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    protected override void AddApplication(IServiceCollection services)
    {
        Environment.SetEnvironmentVariable("APP_DOMAIN", "core");
        Environment.SetEnvironmentVariable("APP_VERSION", "1.0.0");
        RegisterHttpContextAccessor(services);
        base.AddApplication(services);
    }

    /// <summary>
    /// Registers a mock implementation of <see cref="IHttpContextAccessor"/> with predefined headers.
    /// </summary>
    /// <param name="services">The service collection to add the mock to.</param>
    private void RegisterHttpContextAccessor(IServiceCollection services)
    {
        var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        var httpContextMock = new Mock<HttpContext>();
        var requestMock = new Mock<HttpRequest>();
        var headers = new HeaderDictionary
        {
            { "client_id", "test_1" }, { "user_reference", "11111111111" }, { "card_number", "1122334455667788" },
        };

        requestMock.Setup(r => r.Headers).Returns(headers);
        httpContextMock.Setup(h => h.Request).Returns(requestMock.Object);
        httpContextAccessorMock.Setup(h => h.HttpContext).Returns(httpContextMock.Object);
        services.AddSingleton(httpContextAccessorMock.Object);
    }
}