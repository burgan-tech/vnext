using BBT.Aether.MultiSchema;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Data;

/// <summary>
/// Factory for creating <see cref="WorkflowDbContext"/> instances.
/// Injects <see cref="ICurrentSchema"/> so that each context is schema-aware.
/// </summary>
public class WorkflowDbContextFactory(
    DbContextOptions<WorkflowDbContext> options,
    ICurrentSchema currentSchema)
    : IDbContextFactory<WorkflowDbContext>
{
    /// <inheritdoc />
    public WorkflowDbContext CreateDbContext()
    {
        return new WorkflowDbContext(options, currentSchema);
    }
}