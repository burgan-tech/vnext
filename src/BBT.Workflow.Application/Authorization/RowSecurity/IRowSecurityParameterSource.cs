using BBT.Workflow.Definitions.RowSecurity;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// Serves the raw values of a row-security parameter path for the current request. Values are returned
/// untrimmed and unsplit; trimming, splitting and blank handling belong to the planner.
/// </summary>
public interface IRowSecurityParameterSource
{
    /// <summary>The values of <paramref name="path"/>; an empty list means the parameter is missing.</summary>
    /// <param name="path">The parsed parameter path.</param>
    IReadOnlyList<string> Get(RowParameterPath path);
}
