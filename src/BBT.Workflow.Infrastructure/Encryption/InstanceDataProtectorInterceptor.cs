using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BBT.Workflow.Encryption;

/// <summary>
/// Hands every materialized <see cref="InstanceData"/> row the <see cref="IInstanceDataProtector"/> that opens its
/// <c>x-encryption.type: "encrypt"</c> tokens.
/// <para>
/// Decryption itself is deferred to the row's first <see cref="InstanceData.Data"/> read: at this point EF has set
/// the row's scalars (so <see cref="InstanceData.InstanceId"/> — part of the ciphertext's binding — is known) but
/// has NOT yet assigned the owned stored content. Measured across tracking, no-tracking, split, identity-resolution,
/// filtered-include, direct and join-projection queries (council experiment E2). The transition pipeline forces
/// that first read before any task runs, so an undecryptable value is refused before the engine could act on it.
/// </para>
/// </summary>
public sealed class InstanceDataProtectorInterceptor(IInstanceDataProtector protector) : IMaterializationInterceptor
{
    /// <inheritdoc />
    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        // The row's secret lives in the flow schema it was read from; capture it now, while the context is at hand.
        if (entity is InstanceData row)
            row.AttachProtector(protector, (materializationData.Context as BBT.Workflow.Data.WorkflowDbContext)?.CurrentSchemaName);

        return entity;
    }
}
