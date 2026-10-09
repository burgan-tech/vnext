using System.Threading;
using System.Threading.Tasks;

namespace BBT.Workflow.Scripting.Functions;

/// <summary>
/// Reads an x-storage file for a script by its handle coordinates, in any domain. Service-to-service: no caller
/// authorization; the handle must be in the named instance's latest data. A failure throws, so the script fails
/// like any other helper failure.
/// </summary>
public interface IScriptFileReader
{
    /// <summary>Reads <paramref name="file"/> of <paramref name="instance"/> (id or key) in <paramref name="domain"/>/<paramref name="flow"/>.</summary>
    Task<ScriptFile> ReadAsync(string domain, string flow, string instance, string file, CancellationToken cancellationToken);
}
