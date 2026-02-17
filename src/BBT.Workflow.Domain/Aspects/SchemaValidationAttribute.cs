using System.Text.Json;
using BBT.Aether.Aspects;
using BBT.Workflow.Caching;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Instances;
using BBT.Workflow.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PostSharp.Aspects;
using PostSharp.Extensibility;
using PostSharp.Serialization;

namespace BBT.Workflow.Aspects;

/// <summary>
/// Aspect that validates the returned InstanceData against the workflow schema after method execution.
/// Uses the workflow schema from IWorkflowContext.Workflow.
/// </summary>
/// <remarks>
/// This aspect is designed to be used on methods that modify instance data,
/// such as <c>AddData</c> and <c>AddDataWithVersion</c> in the Instance class.
/// 
/// The aspect will:
/// 1. Execute the method first
/// 2. Get the returned InstanceData
/// 3. Get the workflow from IWorkflowContext.Workflow
/// 4. If workflow has a schema, load it from cache
/// 5. Validate the InstanceData.Data against the schema
/// 6. Throw a validation exception if validation fails
/// </remarks>
[PSerializable]
[MulticastAttributeUsage(
    MulticastTargets.Method,
    AllowMultiple = false,
    Inheritance = MulticastInheritance.Strict)]
public class SchemaValidationAttribute : AetherMethodInterceptionAspect
{
    /// <inheritdoc />
    public override async Task OnInvokeAsync(MethodInterceptionArgs args)
    {
        var serviceProvider = GetServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<SchemaValidationAttribute>>();

        try
        {
            await ExecuteAndValidateAsync(args, serviceProvider, logger);
        }
        catch (SchemaValidationException)
        {
            throw; // Re-throw validation exceptions as-is
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during schema validation for {MethodName}",
                args.Method.Name);
            throw;
        }
    }

    /// <inheritdoc />
    public override void OnInvoke(MethodInterceptionArgs args)
    {
        OnInvokeAsync(args).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Executes the method and validates the returned InstanceData.
    /// Gets workflow from IWorkflowContext.Workflow.
    /// </summary>
    private async Task ExecuteAndValidateAsync(
        MethodInterceptionArgs args,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        // Execute the method first
        args.Proceed();

        // Get the returned InstanceData
        if (args.ReturnValue is not InstanceData instanceData)
        {
            logger.LogDebug("Return value is not InstanceData, skipping validation");
            return;
        }
        var workflowContext = serviceProvider.GetRequiredService<IWorkflowContext>();
        var workflow = workflowContext.Workflow;
        if (workflow is null || workflow.Schema is null)
        {
            logger.LogDebug("Skipping schema validation - no workflow in context or no schema available");
            return;
        }
        var componentCacheStore = serviceProvider.GetRequiredService<IComponentCacheStore>();
        // Load schema and validate
        var schemaResult = await componentCacheStore.GetSchemaAsync(workflow.Schema, default);

        if (!schemaResult.IsSuccess)
        {
            logger.LogWarning("Failed to load schema {SchemaKey}: {Error}",
                workflow.Schema.Key, schemaResult.Error.Message);
            return;
        }

        // Validate data against schema
        var dataElement = ExtractJsonElement(instanceData.Data);

        if (!dataElement.HasValue)
        {
            logger.LogDebug("No JSON data to validate in InstanceData");
            return;
        }

        var schemaValidator = serviceProvider.GetRequiredService<IJsonSchemaValidator>();
        var validationResult = schemaValidator.Validate(schemaResult.Value!.Schema, dataElement.Value);

        if (!validationResult.IsSuccess)
        {
            logger.LogWarning("Schema validation failed for {MethodName}: {Error} ValidationErrors: {ValidationErrors}",
                args.Method.Name, validationResult.Error.Message, validationResult.Error.ValidationErrors?.ToList().AsReadOnly());

            throw new SchemaValidationException(
                validationResult.Error.Message ?? "Schema Validation Error",
                validationResult.Error.ValidationErrors?.ToList().AsReadOnly());
        }

        logger.LogDebug("Schema validation passed for {MethodName}", args.Method.Name);
    }

    /// <summary>
    /// Extracts a JsonElement from the data argument.
    /// Supports JsonData, JsonElement
    /// </summary>
    private static JsonElement? ExtractJsonElement(object? dataArg)
    {
        return dataArg switch
        {
            null => null,
            JsonElement element => element,
            JsonData jsonData => jsonData.JsonElement,
            _ => null
        };
    }
}
