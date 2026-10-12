using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Workflow.Data;
using BBT.Workflow.Gateway;
using BBT.Workflow.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Data;

/// <summary>
/// Multi-domain hosting with a dedicated database per co-hosted domain: the configurator decorator, the
/// per-database fan-out, the job bridge and the cross-database gateway boundary. Every one of them must
/// be a pure pass-through when there is only one database.
/// </summary>
public sealed class DomainDatabaseRoutingTests
{
    private const string Default = "Host=db;Database=vNext_Staff";
    private const string Mobile = "Host=db;Database=vNext_Mobile";

    public sealed class TestContext(DbContextOptions<TestContext> options) : DbContext(options);

    private static DomainDatabaseResolver Resolver(bool mobileDedicated)
    {
        var options = new DomainDatabaseOptions { DefaultConnectionString = Default };
        if (mobileDedicated)
        {
            options.Domains["mobile"] = Mobile;
        }

        return new DomainDatabaseResolver(new RuntimeInfoProvider("1.0.0", "mobile,fraud", "staff"), Options.Create(options));
    }

    private static IAetherDbContextConfigurator<TestContext> Inner()
    {
        var inner = Substitute.For<IAetherDbContextConfigurator<TestContext>>();
        inner.CreateConnection().Returns(_ => new NpgsqlConnection(Default));
        inner.BuildOwnedOptions(Arg.Any<string>()).Returns(_ => new DbContextOptionsBuilder<TestContext>()
            .UseNpgsql(Default, npgsql => npgsql.MigrationsHistoryTable("__Workflow_Migrations", "sys_queues"))
            .Options);
        return inner;
    }

    private static string? ConnectionStringOf(DbContextOptions options) =>
        options.FindExtension<NpgsqlOptionsExtension>()?.ConnectionString;

    // ---- configurator ----

    [Fact]
    public void Configurator_DefaultDatabase_PassesEveryCallThrough()
    {
        var inner = Inner();
        var sut = new DomainAwareDbContextConfigurator<TestContext>(inner, Resolver(mobileDedicated: true));

        using (DomainScope.Begin("fraud"))
        {
            sut.CreateConnection().ConnectionString.ShouldBe(Default);
            ConnectionStringOf(sut.BuildOwnedOptions("navigations")).ShouldBe(Default);
        }

        inner.Received(1).CreateConnection();
    }

    [Fact]
    public void Configurator_DedicatedDomain_SwapsOnlyTheConnection()
    {
        var inner = Inner();
        var sut = new DomainAwareDbContextConfigurator<TestContext>(inner, Resolver(mobileDedicated: true));

        using (DomainScope.Begin("mobile"))
        {
            sut.CreateConnection().ConnectionString.ShouldBe(Mobile);

            var options = sut.BuildOwnedOptions("navigations");
            ConnectionStringOf(options).ShouldBe(Mobile);
            options.FindExtension<NpgsqlOptionsExtension>()!.MigrationsHistoryTableName.ShouldBe("__Workflow_Migrations");
            options.FindExtension<NpgsqlOptionsExtension>()!.MigrationsHistoryTableSchema.ShouldBe("sys_queues");
        }

        inner.DidNotReceive().CreateConnection();
    }

    // ---- fan-out ----

    [Fact]
    public async Task FanOut_OneDatabase_RunsOnceWithoutAScope()
    {
        var seen = new List<string?>();

        var total = await DomainDatabaseFanOut.RunAsync(Resolver(mobileDedicated: false), _ =>
        {
            seen.Add(DomainScope.Current);
            return Task.FromResult(3);
        }, CancellationToken.None);

        total.ShouldBe(3);
        seen.ShouldBe([null]);
    }

    [Fact]
    public async Task FanOut_DedicatedDatabase_RunsPerDatabaseUnderItsScope_AndSurvivesAFailure()
    {
        var seen = new List<string?>();

        var act = () => DomainDatabaseFanOut.RunAsync(Resolver(mobileDedicated: true), _ =>
        {
            seen.Add(DomainScope.Current);
            return DomainScope.Current == "staff"
                ? throw new InvalidOperationException("staff database down")
                : Task.FromResult(2);
        }, CancellationToken.None);

        (await Should.ThrowAsync<InvalidOperationException>(act)).Message.ShouldBe("staff database down");
        seen.ShouldBe(["staff", "mobile"]); // mobile still ran after staff failed
    }

    [Fact]
    public async Task OutboxDecorator_SumsEveryDatabase()
    {
        var inner = Substitute.For<IOutboxProcessor>();
        inner.RunAsync(Arg.Any<CancellationToken>()).Returns(1);

        (await new DomainFanOutOutboxProcessor(inner, Resolver(mobileDedicated: true)).RunAsync()).ShouldBe(2);
    }

    // ---- job bridge ----

    [Theory]
    [InlineData("""{"type":"job","schema":"navigations","data":{"domain":"mobile","workflow":"x"}}""", "mobile")]
    [InlineData("""{"type":"job","data":"{\"Domain\":\"fraud\"}"}""", "fraud")]
    [InlineData("""{"domain":"mobile"}""", "mobile")]
    [InlineData("""{"type":"job","data":{"workflow":"x"}}""", null)]
    [InlineData("not json", null)]
    public void JobBridge_ReadsTheDomainFromThePayload(string payload, string? expected)
    {
        DomainScopedJobExecutionBridge.TryReadDomain(Encoding.UTF8.GetBytes(payload)).ShouldBe(expected);
    }

    [Fact]
    public async Task JobBridge_OpensTheScopeBeforeTheInnerBridgeClaims()
    {
        string? seen = "unset";
        var inner = Substitute.For<IJobExecutionBridge>();
        inner.ExecuteAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                seen = DomainScope.Current;
                return Task.CompletedTask;
            });

        await new DomainScopedJobExecutionBridge(inner, Resolver(mobileDedicated: true))
            .ExecuteAsync("job-1", Encoding.UTF8.GetBytes("""{"type":"job","data":{"domain":"mobile"}}"""), CancellationToken.None);

        seen.ShouldBe("mobile");
    }

    // ---- gateway boundary ----

    [Fact]
    public async Task Boundary_SameDatabase_NoNewRoot()
    {
        var uow = Substitute.For<IUnitOfWorkManager>();
        var sut = new CoHostedDatabaseBoundary(Resolver(mobileDedicated: true), uow);

        (await sut.RunAsync("fraud", () => Task.FromResult(DomainScope.Current))).ShouldBe("fraud");

        uow.DidNotReceiveWithAnyArgs().Begin(default!);
    }

    [Fact]
    public async Task Boundary_OtherDatabase_RunsInARequiresNewRootAsTheTarget()
    {
        var uow = Substitute.For<IUnitOfWorkManager>();
        uow.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(Substitute.For<IUnitOfWork>());
        var sut = new CoHostedDatabaseBoundary(Resolver(mobileDedicated: true), uow);

        (await sut.RunAsync("mobile", () => Task.FromResult(DomainScope.Current))).ShouldBe("mobile");

        uow.Received(1).Begin(Arg.Is<UnitOfWorkOptions>(o =>
            o.Scope == UnitOfWorkScopeOption.RequiresNew && !o.IsTransactional));
    }
}
