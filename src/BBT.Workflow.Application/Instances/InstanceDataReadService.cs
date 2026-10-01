using System.Text.Json;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Authorization;

namespace BBT.Workflow.Instances;

/// <summary>
/// Scoped implementation of <see cref="IInstanceDataReadService"/>: the field exposure pass over the row as stored, with
/// the stored form as the fallback. A row carrying <c>encrypt</c> tokens is opened here, on demand, so an exempt caller can
/// be served the plaintext — the row itself is never decrypted in place.
/// </summary>
public sealed class InstanceDataReadService(
    ISchemaFieldFilterService fieldFilter,
    IInstanceSecretPreloader? secretPreloader = null,
    IInstanceDataProtector? protector = null,
    ICurrentSchema? currentSchema = null) : IInstanceDataReadService
{
    /// <inheritdoc />
    public async Task<JsonElement?> ExposeAsync(
        Definitions.Workflow? workflow,
        Instance instance,
        InstanceData? row,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken = default)
    {
        if (row is null)
            return null;

        if (secretPreloader is not null && EncryptedValueFormat.MayContainToken(row.Data.Json))
            await secretPreloader.PreloadAsync([instance.Id], cancellationToken);

        return await ExposeRowAsync(fieldFilter, protector, currentSchema, workflow, instance, row, requestContext, cancellationToken);
    }

    /// <inheritdoc />
    public IInstanceDataPageReader CreatePageReader(IReadOnlyCollection<Instance> page) => new PageReader(
        fieldFilter is IListSchemaFieldFilterFactory factory ? factory.CreateForList() : fieldFilter,
        secretPreloader,
        protector,
        currentSchema,
        page);

    private static readonly IReadOnlyDictionary<string, string> NoTokens = new Dictionary<string, string>(0);

    private static async Task<JsonElement?> ExposeRowAsync(
        ISchemaFieldFilterService filter,
        IInstanceDataProtector? protector,
        ICurrentSchema? currentSchema,
        Definitions.Workflow? workflow,
        Instance instance,
        InstanceData row,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
    {
        // The filter decides per path what this caller sees: it needs the opened value only where the caller is exempt
        // from an encrypt rule, and the stored token everywhere else. A row without tokens is its own plaintext.
        var plain = row.Data;
        var tokens = NoTokens;
        if (protector is not null && EncryptedValueFormat.MayContainToken(row.Data.Json))
        {
            var view = await protector.UnprotectAsync(currentSchema?.Name, instance.Id, row.Data, cancellationToken);
            plain = view.Plain;
            tokens = view.Tokens;
        }

        var exposed = await filter.ApplyAsync(workflow, plain.JsonElement, instance, requestContext, cancellationToken, tokens);
        return exposed ?? row.Data.JsonElement;
    }

    private sealed class PageReader(
        ISchemaFieldFilterService filter,
        IInstanceSecretPreloader? secretPreloader,
        IInstanceDataProtector? protector,
        ICurrentSchema? currentSchema,
        IReadOnlyCollection<Instance> page) : IInstanceDataPageReader
    {
        private bool _preloaded;

        public async Task<JsonElement?> ExposeAsync(
            Definitions.Workflow? workflow,
            Instance instance,
            InstanceData? row,
            AuthorizationRequestContext? requestContext,
            CancellationToken cancellationToken = default)
        {
            if (row is null)
                return null;

            if (!_preloaded)
            {
                _preloaded = true;
                if (secretPreloader is not null)
                {
                    var protectedIds = page
                        .Where(i => EncryptedValueFormat.MayContainToken(i.LatestData?.Data.Json))
                        .Select(i => i.Id)
                        .ToList();
                    if (protectedIds.Count > 0)
                        await secretPreloader.PreloadAsync(protectedIds, cancellationToken);
                }
            }

            return await ExposeRowAsync(filter, protector, currentSchema, workflow, instance, row, requestContext, cancellationToken);
        }
    }
}
