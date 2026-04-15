using BBT.Workflow.Schemas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BBT.Workflow.Data;

/// <summary>
/// Design-time factory used by EF Core tools (migrations, scaffolding).
/// Uses <see cref="StaticCurrentSchema"/> with the default "public" schema so that
/// migration files are generated without a tenant-specific schema prefix.
/// </summary>
public sealed class WorkflowDbContextDesignFactory : IDesignTimeDbContextFactory<WorkflowDbContext>
{
    /// <inheritdoc />
    public WorkflowDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<WorkflowDbContext>();

        optionsBuilder.UseNpgsql(
            "Host=localhost;Port=5432;Database=Aether_WorkflowDb;Username=postgres;Password=postgres;",
            npgsqlOptions => { npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations"); });

        // Design-time uses a null schema so migration DDL has no schema prefix.
        // MultiSchemaNpgsqlMigrationsSqlGenerator strips the "public" prefix during migration apply.
        return new WorkflowDbContext(optionsBuilder.Options, new StaticCurrentSchema("public"));
    }
}