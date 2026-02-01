using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Composite specification that combines multiple specifications.
/// Executes applicable specifications in priority order.
/// Supports early exit for bypass specifications (Resume, Active SubFlow).
/// Follows DDD Specification Pattern for composable business rules.
/// </summary>
public sealed class CompositeTransitionSpecification : ITransitionSpecification
{
    private readonly IEnumerable<ITransitionSpecification> _specifications;
    private readonly ILogger<CompositeTransitionSpecification> _logger;
    
    /// <summary>
    /// Initializes a new instance of CompositeTransitionSpecification.
    /// </summary>
    /// <param name="specifications">Collection of specifications to compose</param>
    /// <param name="logger">Logger for validation diagnostics</param>
    public CompositeTransitionSpecification(
        IEnumerable<ITransitionSpecification> specifications,
        ILogger<CompositeTransitionSpecification> logger)
    {
        _specifications = specifications;
        _logger = logger;
    }
    
    /// <inheritdoc />
    /// <summary>
    /// Composite priority is not used - child specifications define their own priorities.
    /// </summary>
    public int Priority => 0;
    
    /// <inheritdoc />
    /// <summary>
    /// Composite is always applicable - delegates to child specifications.
    /// </summary>
    public bool IsApplicable(TransitionExecutionContext context) => true;
    
    /// <inheritdoc />
    /// <summary>
    /// Validates context by executing all applicable specifications in priority order.
    /// Supports early exit for bypass specifications (Resume, ActiveSubFlow).
    /// Returns first failure or Ok if all specifications pass.
    /// </summary>
    public Result IsSatisfiedBy(TransitionExecutionContext context)
    {
        // Get applicable specifications ordered by priority (lower first)
        var applicableSpecs = _specifications
            .Where(s => s.IsApplicable(context))
            .OrderBy(s => s.Priority)
            .ToList();
        
        // Execute each specification in order
        foreach (var spec in applicableSpecs)
        {
            var result = spec.IsSatisfiedBy(context);
            
            // Bypass specifications (Resume, ActiveSubFlow) use early exit pattern
            // If they return Ok, short-circuit the validation chain
            if (IsBypassSpecification(spec))
            {
                if (result.IsSuccess)
                {
                    _logger.ValidationBypassedBySpecification(spec.Name, context.InstanceId);
                    return Result.Ok(); // Early exit - skip remaining specifications
                }
            }
            
            // If any specification fails, return the error immediately
            if (!result.IsSuccess)
            {
                _logger.ValidationFailedBySpecification(
                    spec.Name, 
                    context.InstanceId, 
                    result.Error.Code,
                    result.Error.Message ?? string.Empty);
                return result;
            }
        }
        
        // All specifications passed
        return Result.Ok();
    }
    
    /// <summary>
    /// Determines if a specification is a bypass specification.
    /// Bypass specifications can short-circuit the validation chain when satisfied.
    /// </summary>
    private static bool IsBypassSpecification(ITransitionSpecification spec)
    {
        return spec is ResumeModeSpecification or SubFlowBypassSpecification;
    }
}
