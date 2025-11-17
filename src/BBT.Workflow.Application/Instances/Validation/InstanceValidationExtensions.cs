using BBT.Workflow.Definitions;
using BBT.Workflow.Domain;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances.Validation;

/// <summary>
/// Extension methods for instance data validation operations.
/// These extensions help centralize validation logic and reduce code duplication.
/// </summary>
public static class InstanceValidationExtensions
{
    /// <summary>
    /// Validates instance data based on the workflow's validation configuration.
    /// This method checks the validation mode and only validates if OnDataChange mode is enabled.
    /// </summary>
    /// <param name="validationService">The validation service instance</param>
    /// <param name="instance">The workflow instance to validate</param>
    /// <param name="workflow">The workflow definition containing validation configuration</param>
    /// <param name="logger">Logger for validation operations</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result indicating validation success or failure</returns>
    public static async Task<Result> ValidateOnDataChangeAsync(
        this IInstanceDataValidationService validationService,
        Instance instance,
        Definitions.Workflow workflow,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Skip validation if not configured or not in OnDataChange mode
        if (workflow.InstanceDataValidation == null ||
            workflow.InstanceDataValidation.ValidationMode != InstanceDataValidationMode.OnDataChange)
        {
            return Result.Ok();
        }

        logger.LogTrace(
            "Validating instance data on data change for instance {InstanceId}",
            instance.Id);

        return await validationService.ValidateInstanceDataAsync(
            instance,
            workflow,
            InstanceDataValidationMode.OnDataChange,
            cancellationToken);
    }

    /// <summary>
    /// Validates instance data in PostExecution mode.
    /// This method checks the validation mode and only validates if PostExecution mode is enabled.
    /// </summary>
    /// <param name="validationService">The validation service instance</param>
    /// <param name="instance">The workflow instance to validate</param>
    /// <param name="workflow">The workflow definition containing validation configuration</param>
    /// <param name="logger">Logger for validation operations</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result indicating validation success or failure</returns>
    public static async Task<Result> ValidatePostExecutionAsync(
        this IInstanceDataValidationService validationService,
        Instance instance,
        Definitions.Workflow workflow,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Skip validation if not configured or not in PostExecution mode
        if (workflow.InstanceDataValidation == null ||
            workflow.InstanceDataValidation.ValidationMode != InstanceDataValidationMode.PostExecution)
        {
            return Result.Ok();
        }

        logger.LogTrace(
            "Validating instance data post execution for instance {InstanceId}",
            instance.Id);

        return await validationService.ValidateInstanceDataAsync(
            instance,
            workflow,
            InstanceDataValidationMode.PostExecution,
            cancellationToken);
    }
}

