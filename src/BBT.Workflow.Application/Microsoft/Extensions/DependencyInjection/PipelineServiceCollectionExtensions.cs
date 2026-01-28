using BBT.Workflow.Execution;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Execution.Handlers;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.PostCommit.Handlers;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Strategies;
using BBT.Workflow.Execution.Transitions.Factory;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Execution.Validation;

namespace Microsoft.Extensions.DependencyInjection;

public static class PipelineServiceCollectionExtensions
{
    /// <summary>
    /// Adds the new transition pipeline architecture services to the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for method chaining.</returns>
    public static IServiceCollection AddPipelineServices(this IServiceCollection services)
    {
        // Core execution service (facade + core executor)
        services.AddScoped<IWorkflowExecutionService, WorkflowExecutionService>();
        services.AddScoped<IWorkflowExecutionCore>(sp => 
            (IWorkflowExecutionCore)sp.GetRequiredService<IWorkflowExecutionService>());
        
        // Transition Runner (owns chaining with isolated scope + UoW per hop)
        services.AddScoped<ITransitionRunner, TransitionRunner>();
        
        // Execution Strategies
        services.AddScoped<IExecutionStrategyFactory, ExecutionStrategyFactory>();
        services.AddScoped<ITransitionStrategy, SyncTransitionStrategy>();
        services.AddScoped<ITransitionStrategy, AsyncTransitionStrategy>();
        
        // Context Factory
        services.AddScoped<ITransitionContextFactory, TransitionContextFactory>();
        services.AddScoped<IContextRefresher, ContextRefresher>();

        // Transition Data Mapping Service
        services.AddScoped<ITransitionDataMapper, TransitionDataMapper>();

        // Validation Services
        services.AddSingleton<ITransitionValidationService, TransitionValidationService>();

        // Evaluation Services
        services.AddScoped<IAutoConditionEvaluator, AutoConditionEvaluator>();

        // Trigger Handlers
        services.AddScoped<ITransitionHandler, ManualTransitionHandler>();
        services.AddScoped<ITransitionHandler, AutomaticTransitionHandler>();
        services.AddScoped<ITransitionHandler, ScheduledTransitionHandler>();
        services.AddScoped<ITransitionHandler, EventTransitionHandler>();
        services.AddScoped<ITransitionHandlerFactory, TransitionHandlerFactory>();

        // Pipeline Steps (registered in execution order)
        services.AddScoped<ITransitionStep, HandleCancelPreflightStep>();
        services.AddScoped<ITransitionStep, HandleUpdateDataPreflightStep>();
        services.AddScoped<ITransitionStep, ForwardToActiveSubflowStep>();
        services.AddScoped<ITransitionStep, SetBusyStep>();
        services.AddScoped<ITransitionStep, CreateTransitionRecordStep>();
        services.AddScoped<ITransitionStep, RunOnExecuteTasksStep>();
        services.AddScoped<ITransitionStep, CancelScheduledJobsStep>();
        services.AddScoped<ITransitionStep, RunOnExitTasksStep>();
        services.AddScoped<ITransitionStep, ChangeStateStep>();
        services.AddScoped<ITransitionStep, RunOnEntryTasksStep>();
        services.AddScoped<ITransitionStep, HandleSubFlowStep>();
        services.AddScoped<ITransitionStep, ClearBusyOnResumeStep>();
        services.AddScoped<ITransitionStep, ScheduleTransitionsStep>();
        services.AddScoped<ITransitionStep, RunAutomaticTransitionsStep>();
        services.AddScoped<ITransitionStep, HandleFinishStep>();
        services.AddScoped<ITransitionStep, FinalizeTransitionStep>();
        services.AddScoped<ITransitionStep, ResolveAvailableStep>();

        // Pipeline
        services.AddScoped<TransitionPipeline>();

        // Error Boundary Services (used by TaskCoordinator for task-level error handling)
        services.AddScoped<IErrorNormalizer, ErrorNormalizer>();
        
        // Post-Commit Execution (jobs run after lock release)
        services.AddScoped<IPostCommitExecutor, PostCommitExecutor>();
        services.AddScoped<IPostCommitHandler<StartSubflowJob>, StartSubflowJobHandler>();
        services.AddScoped<IPostCommitHandler<ForwardToSubflowJob>, ForwardToSubflowJobHandler>();
        services.AddSingleton<IPostCommitFailurePolicy, DefaultPostCommitFailurePolicy>();

        return services;
    }
}
