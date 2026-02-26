using System;
using BBT.Aether;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Runtime;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Runtime;

/// <summary>
/// Unit tests for <see cref="RuntimeInfoProvider"/>.
/// Verifies environment variable reading, fallback behavior, and domain matching logic.
/// </summary>
public class RuntimeInfoProviderTests : IDisposable
{
    private readonly string? originalAppVersion;
    private readonly string? originalAppDomain;

    public RuntimeInfoProviderTests()
    {
        originalAppVersion = Environment.GetEnvironmentVariable("APP_VERSION");
        originalAppDomain = Environment.GetEnvironmentVariable("APP_DOMAIN");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("APP_VERSION", originalAppVersion);
        Environment.SetEnvironmentVariable("APP_DOMAIN", originalAppDomain);
    }

    [Fact]
    public void Constructor_WhenBothEnvVarsSet_ShouldSetVersionAndDomain()
    {
        // Arrange
        Environment.SetEnvironmentVariable("APP_VERSION", "2.5.1");
        Environment.SetEnvironmentVariable("APP_DOMAIN", "test-domain");

        // Act
        var provider = new RuntimeInfoProvider();

        // Assert
        provider.Version.ShouldBe("2.5.1");
        provider.Domain.ShouldBe("test-domain");
    }

    [Fact]
    public void Constructor_WhenAppVersionNotSet_ShouldFallbackToAssemblyVersion()
    {
        // Arrange
        Environment.SetEnvironmentVariable("APP_VERSION", null);
        Environment.SetEnvironmentVariable("APP_DOMAIN", "test-domain");

        // Act
        var provider = new RuntimeInfoProvider();

        // Assert — assembly version is non-null in test runtime, so no exception
        provider.Version.ShouldNotBeNullOrWhiteSpace();
        provider.Version.ShouldNotBe("unknown");
        provider.Domain.ShouldBe("test-domain");
    }

    [Fact]
    public void Constructor_WhenAppDomainNotSet_ShouldThrow()
    {
        // Arrange
        Environment.SetEnvironmentVariable("APP_VERSION", "1.0.0");
        Environment.SetEnvironmentVariable("APP_DOMAIN", null);

        // Act & Assert
        var ex = Should.Throw<AetherException>(() => new RuntimeInfoProvider());
        ex.Message.ShouldContain("APP_VERSION and APP_DOMAIN");
    }

    [Fact]
    public void IsDomainMatch_ShouldBeCaseInsensitive()
    {
        // Arrange
        Environment.SetEnvironmentVariable("APP_VERSION", "1.0.0");
        Environment.SetEnvironmentVariable("APP_DOMAIN", "MyDomain");
        var provider = new RuntimeInfoProvider();

        // Act & Assert
        provider.IsDomainMatch("mydomain").ShouldBeTrue();
        provider.IsDomainMatch("MYDOMAIN").ShouldBeTrue();
        provider.IsDomainMatch("MyDomain").ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsDomainMatch_WhenNullOrEmpty_ShouldReturnFalse(string? requestDomain)
    {
        // Arrange
        Environment.SetEnvironmentVariable("APP_VERSION", "1.0.0");
        Environment.SetEnvironmentVariable("APP_DOMAIN", "test-domain");
        var provider = new RuntimeInfoProvider();

        // Act & Assert
        provider.IsDomainMatch(requestDomain).ShouldBeFalse();
    }

    [Fact]
    public void Check_WhenDomainMismatch_ShouldThrowNotFoundDomainException()
    {
        // Arrange
        Environment.SetEnvironmentVariable("APP_VERSION", "1.0.0");
        Environment.SetEnvironmentVariable("APP_DOMAIN", "expected-domain");
        var provider = new RuntimeInfoProvider();

        // Act & Assert
        Should.Throw<NotFoundDomainException>(() => provider.Check("wrong-domain"));
    }
}
