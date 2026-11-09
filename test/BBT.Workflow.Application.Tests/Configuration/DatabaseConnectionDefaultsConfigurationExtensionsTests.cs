using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Configuration;

/// <summary>
/// The host-level connection-string normalisation: the application name is added when missing, and
/// nothing else in the connection string changes — pool sizing stays with the deployment (PgBouncer).
/// </summary>
public class DatabaseConnectionDefaultsConfigurationExtensionsTests
{
    private const string Plain = "Host=localhost;Port=5432;Database=vNext_Contract;Username=postgres;Password=secret";

    [Fact]
    public void Apply_AddsTheApplicationName_AndChangesNothingElse()
    {
        var result = new NpgsqlConnectionStringBuilder(
            DatabaseConnectionDefaultsConfigurationExtensions.Apply(Plain, "vnext-orchestration"));

        result.ApplicationName.ShouldBe("vnext-orchestration");
        WithoutApplicationName(result).ShouldBe(new NpgsqlConnectionStringBuilder(Plain).ConnectionString);
    }

    [Fact]
    public void Apply_KeepsTheConnectionStringUntouched_WhenItAlreadyNamesTheApplication()
    {
        var explicitString = Plain + ";Application Name=from-helm;Maximum Pool Size=7";

        DatabaseConnectionDefaultsConfigurationExtensions.Apply(explicitString, "vnext-orchestration")
            .ShouldBe(explicitString);
    }

    [Fact]
    public void Apply_KeepsAStringItCannotParse()
    {
        const string unparseable = "this is not a connection string";

        DatabaseConnectionDefaultsConfigurationExtensions.Apply(unparseable, "vnext-orchestration")
            .ShouldBe(unparseable);
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
        });

        configuration.ApplyDatabaseConnectionDefaults("fallback");

        var main = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Default"));
        main.ApplicationName.ShouldBe("vnext-contract-app");
        WithoutApplicationName(main).ShouldBe(new NpgsqlConnectionStringBuilder(Plain).ConnectionString);
        var sales = new NpgsqlConnectionStringBuilder(configuration["ConnectionStrings:Domains:sales"]);
        sales.Database.ShouldBe("vNext_Sales");
        sales.ApplicationName.ShouldBe("vnext-contract-app");
    }

    [Fact]
    public void ApplyDatabaseConnectionDefaults_WithoutAConnectionString_IsANoOp()
    {
        var configuration = new ConfigurationManager();

        configuration.ApplyDatabaseConnectionDefaults("fallback");

        configuration.GetConnectionString("Default").ShouldBeNull();
    }

    /// <summary>The connection string with the added name removed, normalised for comparison.</summary>
    private static string WithoutApplicationName(NpgsqlConnectionStringBuilder builder) =>
        new NpgsqlConnectionStringBuilder(builder.ConnectionString) { ApplicationName = null }.ConnectionString;
}
