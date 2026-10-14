using System.Text.Json;

namespace BBT.Workflow.Instances;

/// <summary>
/// Adds the master schema's runtime <c>x-computed</c> fields (<c>evaluatedBy: "runtime"</c>) to instance data on its way
/// out. Called by <see cref="IInstanceDataReadService"/> on the caller's already-exposed view, so it is applied on every
/// read surface — instance GET / list, data function, data history, sync start / transition output and the
/// GetInstance / GetInstances / GetInstanceData tasks — and a computed value is built only from what the caller sees.
/// Computed fields are never stored.
/// </summary>
public interface IComputedFieldReadService
{
    /// <summary>
    /// Returns <paramref name="exposed"/> with the computed fields written in, or <paramref name="exposed"/> unchanged when
    /// the workflow has no master schema, the schema cannot be loaded or declares no runtime computed field.
    /// </summary>
    /// <param name="workflow">The instance's workflow definition (its <c>schema</c> reference names the master schema).</param>
    /// <param name="exposed">The data as this caller is allowed to see it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<JsonElement> ApplyAsync(Definitions.Workflow? workflow, JsonElement exposed, CancellationToken cancellationToken = default);
}
