using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.DbMigrator;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Schemas.Tests;

/// <summary>
/// Pins the <see cref="SchemaDowngradeRunner"/> gates themselves — the container tests cover the
/// plan/converge primitives, but a silently inverted gate condition (e.g. a dropped
/// <c>!command.AcceptDataLoss</c>) would ship without these: each gate must refuse the WHOLE run
/// before anything is applied, the intent flags must open exactly their own gate, a no-op plan must
/// succeed without applying, and the <c>--script</c> dry run must bypass the acknowledgment gates
/// while still applying nothing.
/// </summary>
public sealed class SchemaDowngradeRunnerTests
{
    private const string Target = "20260901065706_AddSubflowSettlementMarker";
    private const string Schema = "lab";

    private readonly ITargetedSchemaMigrator _targeted = Substitute.For<ITargetedSchemaMigrator>();
    private readonly ISchemaMigrationOrchestrator _orchestrator = Substitute.For<ISchemaMigrationOrchestrator>();
    private readonly RuntimeOptions _runtimeOptions = new();

    private SchemaDowngradeRunner CreateRunner()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_targeted);
        services.AddSingleton(_orchestrator);
        services.AddSingleton(Options.Create(_runtimeOptions));
        var provider = services.BuildServiceProvider();
        return new SchemaDowngradeRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SchemaDowngradeRunner>.Instance);
    }

    private void SetupPlan(SchemaMigrationPlan plan) =>
        _targeted.PlanMigrationToTargetAsync(plan.Schema, Target, Arg.Any<CancellationToken>())
            .Returns(plan);

    private static SchemaMigrationPlan Plan(
        string schema,
        IReadOnlyList<string>? revert = null,
        IReadOnlyList<string>? destructive = null,
        IReadOnlyList<string>? modelBreaking = null,
        IReadOnlyList<string>? unknown = null) =>
        new(schema, Target, "head", revert ?? ["m2", "m1"], [],
            destructive ?? [], modelBreaking ?? [], unknown ?? []);

    private static MigratorCommand Downgrade(
        bool acceptDataLoss = false, bool forRuntimeRollback = false, string? scriptDir = null) => new()
    {
        Kind = MigratorCommandKind.Downgrade,
        Target = Target,
        Schemas = [Schema],
        AcceptDataLoss = acceptDataLoss,
        ForRuntimeRollback = forRuntimeRollback,
        ScriptDirectory = scriptDir
    };

    [Fact]
    public async Task SchemaAheadOfThisBuild_RefusesTheWholeRun_EvenWithBothFlags()
    {
        SetupPlan(Plan(Schema, unknown: ["20991231235959_FromANewerBuild"]));
        var runner = CreateRunner();

        await runner.RunDowngradeAsync(Downgrade(acceptDataLoss: true, forRuntimeRollback: true));

        runner.Success.ShouldBeFalse();
        await _orchestrator.DidNotReceiveWithAnyArgs()
            .MigrateSchemaToTargetWithLockAsync(default!, default!, default);
    }

    [Fact]
    public async Task ModelBreakingRevert_WithoutRollbackIntent_Refuses()
    {
        SetupPlan(Plan(Schema, modelBreaking: ["m2: drops column \"Instances\".\"Type\""]));
        var runner = CreateRunner();

        await runner.RunDowngradeAsync(Downgrade(acceptDataLoss: true));

        runner.Success.ShouldBeFalse();
        await _orchestrator.DidNotReceiveWithAnyArgs()
            .MigrateSchemaToTargetWithLockAsync(default!, default!, default);
    }

    [Fact]
    public async Task ModelBreakingRevert_WithRollbackIntent_Applies()
    {
        SetupPlan(Plan(Schema, modelBreaking: ["m2: drops column \"Instances\".\"Type\""]));
        _orchestrator.MigrateSchemaToTargetWithLockAsync(Schema, Target, Arg.Any<CancellationToken>())
            .Returns(true);
        var runner = CreateRunner();

        await runner.RunDowngradeAsync(Downgrade(forRuntimeRollback: true));

        runner.Success.ShouldBeTrue();
        await _orchestrator.Received(1)
            .MigrateSchemaToTargetWithLockAsync(Schema, Target, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DestructiveRevert_WithoutAcknowledgment_Refuses()
    {
        // Destructive-only (an unmapped legacy table drop): the model gate stays silent, the
        // data-loss gate alone must refuse.
        SetupPlan(Plan(Schema, destructive: ["m2: DROP TABLE \"LegacyBackup\""]));
        var runner = CreateRunner();

        await runner.RunDowngradeAsync(Downgrade(forRuntimeRollback: true));

        runner.Success.ShouldBeFalse();
        await _orchestrator.DidNotReceiveWithAnyArgs()
            .MigrateSchemaToTargetWithLockAsync(default!, default!, default);
    }

    [Fact]
    public async Task DestructiveRevert_WithAcknowledgment_Applies()
    {
        SetupPlan(Plan(Schema, destructive: ["m2: DROP TABLE \"LegacyBackup\""]));
        _orchestrator.MigrateSchemaToTargetWithLockAsync(Schema, Target, Arg.Any<CancellationToken>())
            .Returns(true);
        var runner = CreateRunner();

        await runner.RunDowngradeAsync(Downgrade(acceptDataLoss: true));

        runner.Success.ShouldBeTrue();
        await _orchestrator.Received(1)
            .MigrateSchemaToTargetWithLockAsync(Schema, Target, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoOpPlan_SucceedsWithoutApplyingAnything()
    {
        SetupPlan(new SchemaMigrationPlan(Schema, Target, Target, [], [], [], [], []));
        var runner = CreateRunner();

        await runner.RunDowngradeAsync(Downgrade());

        runner.Success.ShouldBeTrue();
        await _orchestrator.DidNotReceiveWithAnyArgs()
            .MigrateSchemaToTargetWithLockAsync(default!, default!, default);
    }

    [Fact]
    public async Task SchemaMigrationDisabled_Refuses()
    {
        _runtimeOptions.EnableSchemaMigration = false;
        var runner = CreateRunner();

        await runner.RunDowngradeAsync(Downgrade(acceptDataLoss: true, forRuntimeRollback: true));

        runner.Success.ShouldBeFalse();
        await _targeted.DidNotReceiveWithAnyArgs()
            .PlanMigrationToTargetAsync(default!, default!, default);
    }

    [Fact]
    public async Task ScriptDryRun_BypassesTheAcknowledgmentGates_AndAppliesNothing()
    {
        // Breaking + destructive, NO flags: the apply path would refuse twice over — the dry run
        // must still write the reviewable SQL and never touch the orchestrator.
        SetupPlan(Plan(Schema,
            destructive: ["m2: DROP TABLE \"InstanceIncidents\""],
            modelBreaking: ["m2: drops table \"InstanceIncidents\""]));
        _targeted.GenerateScriptToTargetAsync(Schema, Target, Arg.Any<CancellationToken>())
            .Returns("-- rollback sql");
        var scriptDir = Path.Combine(Path.GetTempPath(), $"downgrade-runner-test-{Guid.NewGuid():N}");
        var runner = CreateRunner();

        try
        {
            await runner.RunDowngradeAsync(Downgrade(scriptDir: scriptDir));

            runner.Success.ShouldBeTrue();
            File.ReadAllText(Path.Combine(scriptDir, $"{Schema}.sql")).ShouldBe("-- rollback sql");
            await _orchestrator.DidNotReceiveWithAnyArgs()
                .MigrateSchemaToTargetWithLockAsync(default!, default!, default);
        }
        finally
        {
            if (Directory.Exists(scriptDir))
                Directory.Delete(scriptDir, recursive: true);
        }
    }
}
