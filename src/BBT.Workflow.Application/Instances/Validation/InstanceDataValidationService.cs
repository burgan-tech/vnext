using System.Text.Json;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Domain;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using BBT.Workflow.Validation;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances.Validation;

/// <summary>
/// Provides instance data validation operations using Result Pattern.
/// This service validates instance data against the workflow's master schema.
/// </summary>
/// <param name="componentCacheStore">Cache store for retrieving workflow components like schemas</param>
/// <param name="schemaValidator">Validator for JSON schema validation against instance data</param>
/// <param name="logger">Logger for validation operations</param>
public sealed class InstanceDataValidationService(
    IComponentCacheStore componentCacheStore,
    IJsonSchemaValidator schemaValidator,
    ILogger<InstanceDataValidationService> logger) : IInstanceDataValidationService
{
    /// <inheritdoc />
    public async Task<Result> ValidateInstanceDataAsync(
        Instance instance,
        Definitions.Workflow workflow,
        InstanceDataValidationMode validationMode,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Validating instance data for instance {InstanceId} in {ValidationMode} mode",
            instance.Id, validationMode);

        // 1. Check if workflow has a master schema configured
        if (workflow.Schema == null)
        {
            logger.LogTrace("No master schema configured for workflow {WorkflowKey}, skipping validation",
                workflow.Key);
            return Result.Ok();
        }

        // 2. Load the master schema from component cache
        SchemaDefinition schema;
        try
        {
            schema = await componentCacheStore.GetSchemaAsync(workflow.Schema, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load master schema {SchemaKey} for workflow {WorkflowKey}",
                workflow.Schema.Key, workflow.Key);
            return Result.Fail(
                Error.NotFound(
                    WorkflowErrorCodes.NotFoundInstanceData,
                    $"Master schema '{workflow.Schema.Key}' not found for workflow '{workflow.Key}'",
                    target: workflow.Schema.Key));
        }

        // 3. Retrieve the latest instance data
        var latestData = instance.LatestData;
        if (latestData == null)
        {
            logger.LogTrace("No instance data found for instance {InstanceId}, skipping validation",
                instance.Id);
            return Result.Ok();
        }

        // 4. Convert instance data to JsonElement for validation
        JsonElement? instanceDataJson = null;
        try
        {
            instanceDataJson = latestData.Data.JsonElement;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to convert instance data to JsonElement for instance {InstanceId}",
                instance.Id);
            return Result.Fail(
                Error.Failure(
                    WorkflowErrorCodes.ValidationErrors,
                    $"Failed to parse instance data for validation: {ex.Message}",
                    ex.GetType().Name));
        }

        // 5. Validate the data against the schema
        logger.LogTrace("Validating instance data against master schema for instance {InstanceId}",
            instance.Id);

        var validationResult = schemaValidator.Validate(schema.Schema, instanceDataJson);

        if (!validationResult.IsSuccess)
        {
            logger.LogWarning("Instance data validation failed for instance {InstanceId} in {ValidationMode} mode: {ErrorCode}",
                instance.Id, validationMode, validationResult.Error.Code);

            // Enhance the error with instance-specific information
            return Result.Fail(
                WorkflowErrors.InstanceDataSchemaValidationFailed(
                    instance.Id,
                    validationMode,
                    validationResult.Error.ValidationErrors ?? []));
        }

        logger.LogDebug("Instance data validation completed successfully for instance {InstanceId} in {ValidationMode} mode",
            instance.Id, validationMode);

        return Result.Ok();
    }
}

