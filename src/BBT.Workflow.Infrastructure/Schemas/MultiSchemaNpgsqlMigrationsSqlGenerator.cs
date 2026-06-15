using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace BBT.Workflow.Schemas;

/// <summary>
/// Npgsql migration SQL generator that rewrites all DDL operations to use the target schema
/// instead of the hardcoded <c>"public"</c> schema stored in migration files.
///
/// Migration files were originally generated with <c>schema: "public"</c>. At runtime this
/// generator replaces that value with the schema name read from <see cref="WorkflowDbContext.CurrentSchemaName"/>,
/// which is set via <c>StaticCurrentSchema</c> in <c>MultiSchemaMigrator</c> before
/// <c>MigrateAsync()</c> is called. This ensures each tenant schema receives its own isolated
/// set of tables without any <c>SET search_path</c> directive.
///
/// <para>
/// Schema is intentionally NOT injected via constructor — <see cref="IMigrationsSqlGenerator"/>
/// is resolved inside EF Core's internal DI container which has no knowledge of
/// application-level services such as <c>ICurrentSchema</c>. Instead, the schema is read
/// directly from the active <see cref="WorkflowDbContext"/> instance, which already holds it
/// via its own constructor parameter.
/// </para>
/// </summary>
public sealed class MultiSchemaNpgsqlMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    INpgsqlSingletonOptions npgsqlSingletonOptions)
    : NpgsqlMigrationsSqlGenerator(dependencies, npgsqlSingletonOptions)
{
#pragma warning disable EF1001 // Internal EF Core / Npgsql API usage
#pragma warning restore EF1001

    /// <inheritdoc />
    protected override void Generate(
        MigrationOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder)
    {
        // Resolve schema from the active DbContext, which carries it via ICurrentSchema /
        // StaticCurrentSchema — no DI involvement required.
        var schema = (dependencies.CurrentContext.Context as WorkflowDbContext)?.CurrentSchemaName;

        // Rewrite hardcoded "public" schema in structured DDL operations to the target schema.
        RewriteSchema(operation, schema);

        // For raw SQL operations (MigrationBuilder.Sql), inject SET search_path so that
        // unqualified table/function references resolve to the correct tenant schema.
        // This is migration-path only; runtime queries never hit this code path.
        if (operation is SqlOperation sqlOp && !string.IsNullOrWhiteSpace(schema))
        {
            sqlOp.Sql = $"SET search_path = \"{schema}\";\n{sqlOp.Sql}";
        }

        base.Generate(operation, model, builder);
    }

    /// <summary>
    /// Replaces <c>"public"</c> or <c>null</c> schema references in a migration operation
    /// with <paramref name="targetSchema"/>. Non-public explicit schemas are left untouched.
    /// </summary>
    private static void RewriteSchema(MigrationOperation operation, string? targetSchema)
    {
        switch (operation)
        {
            // TABLES
            case CreateTableOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                break;

            case DropTableOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                break;

            case RenameTableOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                op.NewSchema = Normalize(op.NewSchema, targetSchema);
                break;

            // COLUMNS
            case AddColumnOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                break;

            case AlterColumnOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                break;

            case DropColumnOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                break;

            case RenameColumnOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                break;

            // PRIMARY KEY
            case AddPrimaryKeyOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                break;

            case DropPrimaryKeyOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                break;

            // FOREIGN KEY
            case AddForeignKeyOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                op.PrincipalSchema = Normalize(op.PrincipalSchema, targetSchema);
                // If principal schema is still unset but the table schema is known, use it
                if (string.IsNullOrWhiteSpace(op.PrincipalSchema) && !string.IsNullOrWhiteSpace(op.Schema))
                {
                    op.PrincipalSchema = op.Schema;
                }
                break;

            case DropForeignKeyOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                break;

            // INDEX
            case CreateIndexOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                break;

            case DropIndexOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                break;

            case RenameIndexOperation op:
                op.Schema = Normalize(op.Schema, targetSchema);
                break;

            case EnsureSchemaOperation _:
            case DropSchemaOperation _:
                break;
        }
    }

    /// <summary>
    /// Returns <paramref name="targetSchema"/> when the migration-file schema is <c>null</c>,
    /// empty, or <c>"public"</c>; otherwise returns the original value unchanged.
    /// </summary>
    private static string? Normalize(string? migrationSchema, string? targetSchema)
    {
        if (string.IsNullOrWhiteSpace(migrationSchema) ||
            migrationSchema.Equals("public", StringComparison.OrdinalIgnoreCase))
        {
            return targetSchema;
        }

        return migrationSchema;
    }
}