using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.Validation;

namespace BBT.Workflow.Instances;

/// <inheritdoc />
public sealed class InstanceDataMutationService(
    IComponentCacheStore componentCacheStore,
    IJsonSchemaValidator schemaValidator) : IInstanceDataMutationService
{
    private string? _cachedSchemaIdentity;
    private SchemaDefinition? _cachedSchema;
    private readonly HashSet<Guid> _validatedDataIds = [];

    /// <inheritdoc />
    public async Task<Result<InstanceData>> AddDataAsync(
        Definitions.Workflow workflow,
        Instance instance,
        Guid id,
        JsonData inputData,
        VersionStrategy? versionStrategy = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? headers = null)
    {
        var latestData = instance.LatestData;
        var candidate = latestData is null
            ? inputData
            : latestData.Data.Merge(inputData);
        if (latestData?.HasSameData(candidate) == true)
        {
            // Compare the merged full snapshot, not the incoming delta. A patch that repeats an
            // existing field is semantically a no-op even though its raw JSON differs from the
            // stored full snapshot. Avoid both schema/cache work and a redundant history row.
            return Result<InstanceData>.Ok(latestData);
        }

        if (workflow.Schema is null)
        {
            return Result<InstanceData>.Ok(instance.AddData(id, inputData, versionStrategy));
        }

        var validationResult = await ValidateAsync(workflow.Schema, candidate, headers, cancellationToken);
        if (!validationResult.IsSuccess)
        {
            return Result<InstanceData>.Fail(validationResult.Error);
        }

        var added = instance.AddData(id, inputData, versionStrategy);
        _validatedDataIds.Add(added.Id);
        return Result<InstanceData>.Ok(added);
    }

    /// <inheritdoc />
    public async Task<Result<InstanceData>> AddDataWithVersionAsync(
        Definitions.Workflow workflow,
        Instance instance,
        Guid id,
        JsonData inputData,
        string version,
        bool ignoreSameData = true,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? headers = null)
    {
        if (workflow.Schema is null)
        {
            return Result<InstanceData>.Ok(
                instance.AddDataWithVersion(id, inputData, version, ignoreSameData));
        }

        var validationResult = await ValidateAsync(workflow.Schema, inputData, headers, cancellationToken);
        if (!validationResult.IsSuccess)
        {
            return Result<InstanceData>.Fail(validationResult.Error);
        }

        var added = instance.AddDataWithVersion(id, inputData, version, ignoreSameData);
        _validatedDataIds.Add(added.Id);
        return Result<InstanceData>.Ok(added);
    }

    /// <inheritdoc />
    public async Task<Result> ApplyScriptContextChangesAsync(
        Definitions.Workflow workflow,
        TransitionExecutionContext transitionContext,
        ScriptContext scriptContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(transitionContext);
        ArgumentNullException.ThrowIfNull(scriptContext);

        var scriptInstance = scriptContext.Instance;
        var liveInstance = transitionContext.Instance;
        if (scriptInstance is null || liveInstance is null)
            return Result.Ok();

        var existingIds = liveInstance.DataList
            .Select(data => data.Id)
            .ToHashSet();
        var pendingVersions = scriptInstance.DataList
            .Where(data => !existingIds.Contains(data.Id))
            .ToList();

        // Validate the entire batch before mutating the tracked aggregate. Every InstanceData row
        // is a complete snapshot, so validating each one also protects immutable history from an
        // invalid intermediate version produced by scripts or parallel-branch merging.
        if (workflow.Schema is not null && pendingVersions.Count > 0)
        {
            var schemaResult = await GetSchemaAsync(workflow.Schema, cancellationToken);
            if (!schemaResult.IsSuccess)
                return Result.Fail(schemaResult.Error);

            var options = CreateValidationOptions(transitionContext.Headers);
            foreach (var data in pendingVersions.Where(data => !_validatedDataIds.Contains(data.Id)))
            {
                var validationResult = schemaValidator.Validate(
                    schemaResult.Value!.Schema,
                    data.Data.JsonElement,
                    options);
                if (!validationResult.IsSuccess)
                    return validationResult;

                _validatedDataIds.Add(data.Id);
            }
        }

        foreach (var data in pendingVersions)
        {
            liveInstance.AddDataWithVersion(
                data.Id,
                new JsonData(data.Data.Json),
                data.Version);
        }

        if (pendingVersions.Count > 0)
            transitionContext.Data = liveInstance.Data;

        if (scriptContext.Mutations.HasChanges)
            scriptContext.Mutations.ApplyTo(liveInstance);

        return Result.Ok();
    }

    private async Task<Result> ValidateAsync(
        IReference schemaReference,
        JsonData candidate,
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken)
    {
        var schemaResult = await GetSchemaAsync(schemaReference, cancellationToken);

        if (!schemaResult.IsSuccess)
        {
            return Result.Fail(schemaResult.Error);
        }

        return schemaValidator.Validate(
            schemaResult.Value!.Schema,
            candidate.JsonElement,
            CreateValidationOptions(headers));
    }

    private async Task<Result<SchemaDefinition>> GetSchemaAsync(
        IReference schemaReference,
        CancellationToken cancellationToken)
    {
        var identity = string.Join('\n',
            schemaReference.Domain,
            schemaReference.Key,
            schemaReference.Version);
        if (_cachedSchema is not null
            && string.Equals(_cachedSchemaIdentity, identity, StringComparison.Ordinal))
        {
            return Result<SchemaDefinition>.Ok(_cachedSchema);
        }

        var schemaResult = await componentCacheStore.GetSchemaAsync(
            schemaReference,
            cancellationToken);
        if (schemaResult.IsSuccess)
        {
            if (!string.Equals(_cachedSchemaIdentity, identity, StringComparison.Ordinal))
                _validatedDataIds.Clear();
            _cachedSchemaIdentity = identity;
            _cachedSchema = schemaResult.Value!;
        }

        return schemaResult;
    }

    private static SchemaValidationOptions CreateValidationOptions(
        IReadOnlyDictionary<string, string?>? headers)
        => new(
            Culture: LanguageResolver.ResolveCulture(headers),
            IncludeVocabularyDetails: true,
            CustomValidationEnabled: true);
}
