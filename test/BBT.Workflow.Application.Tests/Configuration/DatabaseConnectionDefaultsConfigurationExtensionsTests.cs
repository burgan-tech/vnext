using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Configuration;

/// <summary>
/// The host-level connection-string normalisation: an optional connection role
/// (<c>ConnectionStrings:&lt;role&gt;</c>) replaces Default for the process, the application name is added
/// when missing, and nothing else in the connection string changes.
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

    [Fact]
    public void ApplyDatabaseConnectionDefaults_RoleKeySet_ReplacesDefaultForThisProcess()
    {
        var configuration = Shared(inbox: Plain.Replace("Username=postgres", "Username=vnext_inbox"));

        configuration.ApplyDatabaseConnectionDefaults(
            "vnext-worker-inbox", DatabaseConnectionDefaultsConfigurationExtensions.InboxRole);

        var main = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Default"));
        main.Username.ShouldBe("vnext_inbox");
        main.ApplicationName.ShouldBe("vnext-worker-inbox");
        // Co-hosted domain databases are not part of the role override.
        new NpgsqlConnectionStringBuilder(configuration["ConnectionStrings:Domains:sales"]).Username.ShouldBe("postgres");
    }

    [Theory]
    [InlineData(DatabaseConnectionDefaultsConfigurationExtensions.OutboxRole)]
    [InlineData(DatabaseConnectionDefaultsConfigurationExtensions.MigratorRole)]
    [InlineData(null)]
    public void ApplyDatabaseConnectionDefaults_RoleKeyMissing_FallsBackToDefault(string? role)
    {
        // Only Inbox has its own key: every other role (and the role-less orchestration) keeps Default.
        var configuration = Shared(inbox: Plain.Replace("Username=postgres", "Username=vnext_inbox"));

        configuration.ApplyDatabaseConnectionDefaults("vnext-host", role);

        new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Default")).Username.ShouldBe("postgres");
    }

    [Fact]
    public void ApplyDatabaseConnectionDefaults_BlankRoleKey_FallsBackToDefault()
    {
        var configuration = Shared(inbox: "  ");

        configuration.ApplyDatabaseConnectionDefaults(
            "vnext-worker-inbox", DatabaseConnectionDefaultsConfigurationExtensions.InboxRole);

        new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Default")).Username.ShouldBe("postgres");
    }

    [Fact]
    public void ApplyDatabaseConnectionDefaults_RoleKeyWithoutDefault_UsesTheRoleKey()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Migrator"] = Plain.Replace("Username=postgres", "Username=vnext_ddl"),
        });

        configuration.ApplyDatabaseConnectionDefaults(
            "vnext-db-migrator", DatabaseConnectionDefaultsConfigurationExtensions.MigratorRole);

        new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Default")).Username.ShouldBe("vnext_ddl");
    }

    [Fact]
    public void ApplyDatabaseConnectionDefaults_RoleKeyFromALaterSource_WinsOverAnEarlierDefault()
    {
        // Mirrors the hosts: appsettings/env supply Default, the Vault secret store is added afterwards.
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = Plain,
        });
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = Plain.Replace("Username=postgres", "Username=vault_default"),
            ["ConnectionStrings:Outbox"] = Plain + ";Maximum Pool Size=200;Application Name=outbox-explicit",
        });

        configuration.ApplyDatabaseConnectionDefaults(
            "vnext-worker-outbox", DatabaseConnectionDefaultsConfigurationExtensions.OutboxRole);

        var main = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Default"));
        main.MaxPoolSize.ShouldBe(200);
        main.ApplicationName.ShouldBe("outbox-explicit");
    }

    private static ConfigurationManager Shared(string inbox)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = Plain,
            ["ConnectionStrings:Inbox"] = inbox,
            ["ConnectionStrings:Domains:sales"] = Plain.Replace("vNext_Contract", "vNext_Sales"),
        });
        return configuration;
    }

    /// <summary>The connection string with the added name removed, normalised for comparison.</summary>
    private static string WithoutApplicationName(NpgsqlConnectionStringBuilder builder) =>
        new NpgsqlConnectionStringBuilder(builder.ConnectionString) { ApplicationName = null }.ConnectionString;
}
