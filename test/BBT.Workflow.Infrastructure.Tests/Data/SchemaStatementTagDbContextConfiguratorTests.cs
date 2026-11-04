using System;
using System.Data.Common;
using System.Linq;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Data;

/// <summary>
/// <c>TagStatementsWithSchema&lt;T&gt;()</c> adds the span-tag interceptor to every unit-of-work context and
/// leaves the rest of the inner configurator's options, and its connection, as they were.
/// </summary>
public sealed class SchemaStatementTagDbContextConfiguratorTests
{
    private const string ConnectionString = "Host=db;Database=vNext_Test";

    public sealed class TestContext(DbContextOptions<TestContext> options) : DbContext(options);

    private sealed class MarkerInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor;

    private static readonly MarkerInterceptor Marker = new();

    private static DbContextOptions<TestContext> InnerOptions() =>
        new DbContextOptionsBuilder<TestContext>()
            .UseNpgsql(ConnectionString)
            .AddInterceptors(Marker)
            .Options;

    private static (IAetherDbContextConfigurator<TestContext> Inner, IAetherDbContextConfigurator<TestContext> Sut) Build()
    {
        var inner = Substitute.For<IAetherDbContextConfigurator<TestContext>>();
        inner.CreateConnection().Returns(_ => new NpgsqlConnection(ConnectionString));
        inner.BuildOwnedOptions(Arg.Any<string>()).Returns(_ => InnerOptions());
        inner.BuildOptions(Arg.Any<DbConnection>(), Arg.Any<string>(), Arg.Any<SchemaScopeState>()).Returns(_ => InnerOptions());

        var services = new ServiceCollection();
        services.AddScoped(_ => inner);
        services.TagStatementsWithSchema<TestContext>();

        var sut = services.BuildServiceProvider().CreateScope().ServiceProvider
            .GetRequiredService<IAetherDbContextConfigurator<TestContext>>();
        return (inner, sut);
    }

    private static object[] Interceptors(DbContextOptions options) =>
        options.FindExtension<CoreOptionsExtension>()!.Interceptors!.Cast<object>().ToArray();

    [Fact]
    public void OwnedOptions_AppendTheTagInterceptor_AfterTheInnerOnes()
    {
        var (_, sut) = Build();

        var interceptors = Interceptors(sut.BuildOwnedOptions("sys_queues"));

        interceptors.Length.ShouldBe(2);
        interceptors[0].ShouldBeSameAs(Marker);
        interceptors[1].ShouldBeOfType<SchemaPlaceholderStatementTagInterceptor>();
    }

    [Fact]
    public void SharedOptions_AppendTheTagInterceptor_AndKeepTheConnectionString()
    {
        var (_, sut) = Build();
        using var connection = new NpgsqlConnection(ConnectionString);

        var options = sut.BuildOptions(connection, "sys_queues", default!);

        Interceptors(options).Last().ShouldBeOfType<SchemaPlaceholderStatementTagInterceptor>();
        options.FindExtension<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal.NpgsqlOptionsExtension>()!
            .ConnectionString.ShouldBe(ConnectionString);
    }

    [Fact]
    public void CreateConnection_IsPassedThrough()
    {
        var (inner, sut) = Build();

        sut.CreateConnection().ConnectionString.ShouldBe(ConnectionString);

        inner.Received(1).CreateConnection();
    }

    [Fact]
    public void WithoutAnAetherConfigurator_Throws()
    {
        Should.Throw<InvalidOperationException>(() => new ServiceCollection().TagStatementsWithSchema<TestContext>());
    }
}
