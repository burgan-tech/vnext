using System.Threading;
using System.Threading.Tasks;

namespace BBT.Workflow.Security;

/// <summary>
/// Dynamic schema validator that validates against active flows in the system
/// Schema naming convention: lowercase with underscores (e.g., sys_flows, parent_flow)
/// </summary>
public interface ISchemaValidator
{
    /// <summary>
    /// Validates schema name against active flows (async with caching)
    /// </summary>
    /// <param name="schema">Schema name to validate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validated and cleaned schema name</returns>
    /// <exception cref="System.Security.SecurityException">Thrown when schema is invalid or not authorized</exception>
    Task<string> ValidateSchemaAsync(string? schema, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates schema name synchronously (only validates format, not existence)
    /// Use this for hot paths where DB lookup is not acceptable
    /// </summary>
    /// <param name="schema">Schema name to validate</param>
    /// <returns>Validated and cleaned schema name</returns>
    /// <exception cref="System.Security.SecurityException">Thrown when schema format is invalid</exception>
    string ValidateSchemaSync(string? schema);

    /// <summary>
    /// Validates table name against whitelist
    /// </summary>
    /// <param name="tableName">Table name to validate</param>
    /// <returns>Validated and cleaned table name</returns>
    /// <exception cref="System.Security.SecurityException">Thrown when table name is invalid</exception>
    string ValidateTableName(string? tableName);

    /// <summary>
    /// Invalidates the schema cache (call after flow creation/deletion)
    /// </summary>
    Task InvalidateCacheAsync(CancellationToken cancellationToken = default);
}

