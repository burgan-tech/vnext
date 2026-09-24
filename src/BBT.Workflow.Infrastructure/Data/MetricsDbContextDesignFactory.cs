using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BBT.Workflow.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can build <see cref="MetricsDbContext"/>. The
/// migrations history table is pinned to the fixed <c>sys_metrics</c> schema, mirroring
/// <see cref="MessagingDbContextDesignFactory"/>.
/// </summary>
public sealed class MetricsDbContextDesignFactory : IDesignTimeDbContextFactory<MetricsDbContext>
{
    public MetricsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MetricsDbContext>();

        optionsBuilder.UseNpgsql(
            "Host=localhost;Port=5432;Database=Aether_WorkflowDb;Username=postgres;Password=postgres;",
            npgsqlOptions =>
            {
                npgsqlOptions.MigrationsHistoryTable("__Workflow_Migrations", "sys_metrics");
            });

        return new MetricsDbContext(
            optionsBuilder.Options
        );
    }
}
