using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Configuration;

/// <summary>
/// The host-level connection-string normalisation: application name always, pool limits only from
/// configuration, and an explicit value in the connection string always wins.
/// </summary>
public class DatabaseConnectionDefaultsConfigurationExtensionsTests
{
    private const string Plain = "Host=localhost;Port=5432;Database=vNext_Contract;Username=postgres;Password=secret";

    [Fact]
    public void Apply_AddsTheApplicationName_AndLeavesThePoolAloneWithoutConfiguration()
    {
        var result = new NpgsqlConnectionStringBuilder(
            DatabaseConnectionDefaultsConfigurationExtensions.Apply(Plain, "vnext-orchestration", null, null));

        result.ApplicationName.ShouldBe("vnext-orchestration");
        result.MaxPoolSize.ShouldBe(new NpgsqlConnectionStringBuilder().MaxPoolSize);
        result.Database.ShouldBe("vNext_Contract");
        result.Password.ShouldBe("secret");
    }

    [Fact]
    public void Apply_SetsPoolLimits_FromConfiguration()
    {
        var result = new NpgsqlConnectionStringBuilder(
            DatabaseConnectionDefaultsConfigurationExtensions.Apply(Plain, "inbox", 20, 60));

        result.MaxPoolSize.ShouldBe(20);
        result.ConnectionIdleLifetime.ShouldBe(60);
    }

    [Fact]
    public void Apply_KeepsValuesTheConnectionStringAlreadyStates()
    {
        var explicitString = Plain + ";Application Name=from-helm;Maximum Pool Size=7;Connection Idle Lifetime=11";

        var result = new NpgsqlConnectionStringBuilder(
            DatabaseConnectionDefaultsConfigurationExtensions.Apply(explicitString, "vnext-orchestration", 20, 60));

        result.ApplicationName.ShouldBe("from-helm");
        result.MaxPoolSize.ShouldBe(7);
        result.ConnectionIdleLifetime.ShouldBe(11);
    }

    [Fact]
    public void Apply_ClampsMinPoolSizeToTheNewMaximum()
    {
        var result = new NpgsqlConnectionStringBuilder(
            DatabaseConnectionDefaultsConfigurationExtensions.Apply(Plain + ";Minimum Pool Size=30", null, 10, null));

        result.MaxPoolSize.ShouldBe(10);
        result.MinPoolSize.ShouldBe(10);
    }

    [Fact]
    public void ApplyDatabaseConnectionDefaults_RewritesDefaultAndDomainStrings_PreferringTheServiceName()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = Plain,
            ["ConnectionStrings:Domains:sales"] = Plain.Replace("vNext_Contract", "vNext_Sales"),
            ["OTEL_SERVICE_NAME"] = "vnext-contract-app",
            ["DAPR_APP_ID"] = "ignored-because-otel-wins",
            ["Database:MaxPoolSize"] = "25",
        });

        configuration.ApplyDatabaseConnectionDefaults("fallback");

        var main = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Default"));
        main.ApplicationName.ShouldBe("vnext-contract-app");
        main.MaxPoolSize.ShouldBe(25);
        var sales = new NpgsqlConnectionStringBuilder(configuration["ConnectionStrings:Domains:sales"]);
        sales.Database.ShouldBe("vNext_Sales");
        sales.MaxPoolSize.ShouldBe(25);
    }

    [Fact]
    public void ApplyDatabaseConnectionDefaults_WithoutAConnectionString_IsANoOp()
    {
        var configuration = new ConfigurationManager();

        configuration.ApplyDatabaseConnectionDefaults("fallback");

        configuration.GetConnectionString("Default").ShouldBeNull();
    }
}
