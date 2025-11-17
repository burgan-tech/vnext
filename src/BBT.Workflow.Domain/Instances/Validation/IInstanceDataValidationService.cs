using BBT.Workflow.Definitions;
using BBT.Workflow.Domain;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Instances.Validation;

/// <summary>
/// Interface for instance data validation operations using Result Pattern.
/// This service validates instance data against the workflow's master schema.
/// </summary>
public interface IInstanceDataValidationService
{
    /// <summary>
    /// Validates instance data against the master schema using Result Pattern.
    /// Returns Result.Ok() if validation passes, or Result.Fail() with detailed error information on failure.
    /// </summary>
    /// <param name="instance">The workflow instance to validate</param>
    /// <param name="workflow">The workflow definition containing the master schema reference</param>
    /// <param name="validationMode">The validation mode that triggered this validation</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Result indicating validation success or failure with detailed error information</returns>
    /// <remarks>
    /// This method performs validation steps using Result pattern:
    /// 1. Checks if the workflow has a master schema configured
    /// 2. Loads the master schema from the component cache
    /// 3. Retrieves the latest instance data
    /// 4. Validates the data against the schema using the JSON schema validator
    /// All validations use Result pattern and provide detailed error information for client consumption.
    /// </remarks>
    Task<Result> ValidateInstanceDataAsync(
        Instance instance,
        Definitions.Workflow workflow,
        InstanceDataValidationMode validationMode,
        CancellationToken cancellationToken = default);
}

