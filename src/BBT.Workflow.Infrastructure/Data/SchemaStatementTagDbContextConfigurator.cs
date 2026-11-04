using System.Data.Common;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Data;

/// <summary>
/// Gives every unit-of-work context of <typeparamref name="TDbContext"/> a
/// <see cref="SchemaPlaceholderStatementTagInterceptor"/>, so the spans of its placeholder-model commands
/// name the schema they ran against.
/// </summary>
/// <remarks>
/// Decorates the configurator <c>AddAetherNpgsql</c> registered; registered by
/// <c>TagStatementsWithSchema&lt;TDbContext&gt;()</c>. The interceptor is appended (<c>AddInterceptors</c>
/// concatenates), so Aether's interceptors and every other option of the inner configurator stay as they
/// are. It changes only the span tag, never the command or the connection.
/// </remarks>
public sealed class SchemaStatementTagDbContextConfigurator<TDbContext>(
    IAetherDbContextConfigurator<TDbContext> inner) : IAetherDbContextConfigurator<TDbContext>
    where TDbContext : DbContext
{
    /// <inheritdoc />
    public DbConnection CreateConnection() => inner.CreateConnection();

    /// <inheritdoc />
    public DbContextOptions<TDbContext> BuildOptions(DbConnection sharedConnection, string schema, SchemaScopeState state) =>
        WithStatementTag(inner.BuildOptions(sharedConnection, schema, state), schema);

    /// <inheritdoc />
    public DbContextOptions<TDbContext> BuildOwnedOptions(string schema) =>
        WithStatementTag(inner.BuildOwnedOptions(schema), schema);

    private static DbContextOptions<TDbContext> WithStatementTag(DbContextOptions<TDbContext> options, string schema) =>
        new DbContextOptionsBuilder<TDbContext>(options)
            .AddInterceptors(new SchemaPlaceholderStatementTagInterceptor(schema))
            .Options;
}
