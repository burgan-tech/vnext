using System;
using System.Collections.Generic;
using BBT.Workflow.DbMigrator;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Schemas.Tests;

/// <summary>
/// Pins the DbMigrator invocation contract: no arguments keeps the historical forward-migrate
/// behavior byte-for-byte (deploy templates pass none), arguments win over the
/// <c>DbMigrator:*</c> configuration fallbacks, and invalid invocations fail fast instead of
/// silently running a forward migration.
/// </summary>
public sealed class MigratorCommandTests
{
    [Fact]
    public void NoArgumentsAndNoConfiguration_IsThePlainForwardMigrate()
    {
        var command = MigratorCommand.Parse([], EmptyConfiguration());

        command.Kind.ShouldBe(MigratorCommandKind.Migrate);
        command.Target.ShouldBeNull();
        command.MessagingTarget.ShouldBeNull();
        command.Schemas.ShouldBeEmpty();
        command.ScriptDirectory.ShouldBeNull();
        command.AcceptDataLoss.ShouldBeFalse();
    }

    [Fact]
    public void DowngradeArguments_ParseIntoTheCommand()
    {
        var command = MigratorCommand.Parse(
            ["downgrade", "--target", "20260901065706_AddSubflowSettlementMarker",
             "--schema", "sys_flows", "--schema", "morph-touch",
             "--script", "/tmp/out", "--accept-data-loss", "--for-runtime-rollback"],
            EmptyConfiguration());

        command.Kind.ShouldBe(MigratorCommandKind.Downgrade);
        command.Target.ShouldBe("20260901065706_AddSubflowSettlementMarker");
        command.Schemas.ShouldBe(new[] { "sys_flows", "morph-touch" });
        command.ScriptDirectory.ShouldBe("/tmp/out");
        command.AcceptDataLoss.ShouldBeTrue();
        command.ForRuntimeRollback.ShouldBeTrue();
    }

    [Fact]
    public void ConfigurationDrivesTheCommand_WhenNoArgumentsArePassed()
    {
        // Deployment templates cannot pass container args today — env vars must be able to carry
        // the whole invocation (DbMigrator__Command=downgrade etc.).
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DbMigrator:Command"] = "downgrade",
                ["DbMigrator:Target"] = "SomeMigration",
                ["DbMigrator:MessagingTarget"] = "SomeMessagingMigration",
                ["DbMigrator:Schemas"] = "sys_flows, morph-touch",
                ["DbMigrator:AcceptDataLoss"] = "true",
                ["DbMigrator:ForRuntimeRollback"] = "true"
            }).Build();

        var command = MigratorCommand.Parse([], configuration);

        command.Kind.ShouldBe(MigratorCommandKind.Downgrade);
        command.Target.ShouldBe("SomeMigration");
        command.MessagingTarget.ShouldBe("SomeMessagingMigration");
        command.Schemas.ShouldBe(new[] { "sys_flows", "morph-touch" });
        command.AcceptDataLoss.ShouldBeTrue();
        command.ForRuntimeRollback.ShouldBeTrue();
    }

    [Fact]
    public void Arguments_WinOverConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DbMigrator:Command"] = "status",
                ["DbMigrator:Target"] = "FromConfig"
            }).Build();

        var command = MigratorCommand.Parse(["downgrade", "--target", "FromArgs"], configuration);

        command.Kind.ShouldBe(MigratorCommandKind.Downgrade);
        command.Target.ShouldBe("FromArgs");
    }

    [Theory]
    [InlineData("downgrade")]                          // downgrade without any target
    [InlineData("frobnicate")]                         // unknown command
    [InlineData("--target")]                           // option without a value
    [InlineData("status", "--target", "X")]            // downgrade-only option on another command
    [InlineData("migrate", "--script", "/tmp/out")]    // ditto
    [InlineData("--unknown-option")]
    public void InvalidInvocations_FailFastWithUsage(params string[] args)
    {
        var ex = Should.Throw<ArgumentException>(() => MigratorCommand.Parse(args, EmptyConfiguration()));
        ex.Message.ShouldContain("Usage:");
    }

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();
}
