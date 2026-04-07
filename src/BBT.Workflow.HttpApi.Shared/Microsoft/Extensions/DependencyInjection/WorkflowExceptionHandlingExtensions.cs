using System.Net;
using BBT.Aether.AspNetCore.ExceptionHandling;
using BBT.Workflow;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Exception handling module: maps workflow error codes to HTTP status codes.
/// </summary>
public static class WorkflowExceptionHandlingExtensions
{
    /// <summary>
    /// Configures Aether's error code to HTTP status code mapping for all workflow error codes.
    /// Used by all API hosts that return HTTP responses.
    /// </summary>
    public static IServiceCollection AddWorkflowExceptionHandling(this IServiceCollection services)
    {
        services.Configure<AetherExceptionHttpStatusCodeOptions>(opt =>
        {
            opt.Map(WorkflowErrorCodes.Locked, HttpStatusCode.Conflict);
            opt.Map(WorkflowErrorCodes.ValidationErrors, HttpStatusCode.BadRequest);

            opt.Map(WorkflowErrorCodes.NotFoundDomain, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.ConflictWorkflow, HttpStatusCode.Conflict);
            opt.Map(WorkflowErrorCodes.RuntimeSchemaInvalidState, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.TransitionLocked, HttpStatusCode.Conflict);
            opt.Map(WorkflowErrorCodes.AutoTransitionConditionNotMet, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.UnauthorizedTransition, HttpStatusCode.Forbidden);
            opt.Map(WorkflowErrorCodes.AuthorizationRoleDenied, HttpStatusCode.Forbidden);
            opt.Map(WorkflowErrorCodes.AuthorizeRequiresExactlyOneTarget, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.AuthorizeQueryRolesRequiresInstance, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.InvalidState, HttpStatusCode.BadRequest);
            opt.Map(WorkflowErrorCodes.NotFoundTransition, HttpStatusCode.NotFound);
            opt.Map(WorkflowErrorCodes.NotFoundInitialState, HttpStatusCode.NotFound);
            opt.Map(WorkflowErrorCodes.NotFoundWorkflow, HttpStatusCode.NotFound);

            opt.Map(WorkflowErrorCodes.ExecutionStepFailed, HttpStatusCode.BadRequest);

            opt.Map(WorkflowErrorCodes.TaskContextCreation, HttpStatusCode.InternalServerError);
            opt.Map(WorkflowErrorCodes.TaskExecution, HttpStatusCode.InternalServerError);
        });
        return services;
    }
}
