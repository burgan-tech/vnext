using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Resolves error boundary policies using hierarchical lookup: Task -> State -> Global.
/// Uses CompiledBoundaryChain for efficient rule matching.
/// </summary>
public sealed class ErrorBoundaryResolver : IErrorBoundaryResolver
{
    private readonly ILogger<ErrorBoundaryResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the ErrorBoundaryResolver.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public ErrorBoundaryResolver(ILogger<ErrorBoundaryResolver> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public BoundaryResolutionResult Resolve(
        NormalizedError error,
        CompiledBoundaryChain boundaryChain)
    {
        if (!boundaryChain.HasAnyBoundary)
        {
            _logger.LogDebug(
                "No error boundaries defined in chain. Error: {ErrorCode}",
                error.Code);
            return BoundaryResolutionResult.Unhandled();
        }

        var match = boundaryChain.FindMatch(error);

        if (match == null)
        {
            _logger.LogDebug(
                "No matching boundary rule found for error. Code: {ErrorCode}, ExceptionType: {ExceptionType}, StatusCode: {StatusCode}",
                error.Code,
                error.ExceptionType,
                error.StatusCode);
            return BoundaryResolutionResult.Unhandled();
        }

        var (rule, level) = match.Value;

        _logger.LogInformation(
            "Error boundary matched. Level: {Level}, Action: {Action}, Priority: {Priority}, Code: {ErrorCode}",
            level,
            rule.Rule.Action,
            rule.Rule.Priority,
            error.Code);

        return BoundaryResolutionResult.Handled(rule, level);
    }

    /// <inheritdoc />
    public BoundaryResolutionResult Resolve(
        NormalizedError error,
        ErrorBoundary? taskBoundary,
        ErrorBoundary? stateBoundary,
        ErrorBoundary? globalBoundary)
    {
        // Compile boundaries on-the-fly
        var chain = CompiledBoundaryChain.Compile(taskBoundary, stateBoundary, globalBoundary);
        return Resolve(error, chain);
    }

    /// <inheritdoc />
    public BoundaryResolutionResult ResolveExcluding(
        NormalizedError error,
        CompiledBoundaryChain boundaryChain,
        params ErrorAction[] excludeActions)
    {
        if (!boundaryChain.HasAnyBoundary)
        {
            _logger.LogDebug(
                "No error boundaries defined in chain. Error: {ErrorCode}",
                error.Code);
            return BoundaryResolutionResult.Unhandled();
        }

        var match = boundaryChain.FindMatchExcluding(error, excludeActions);

        if (match == null)
        {
            _logger.LogDebug(
                "No matching boundary rule found (excluding {ExcludedActions}). Code: {ErrorCode}",
                string.Join(", ", excludeActions),
                error.Code);
            return BoundaryResolutionResult.Unhandled();
        }

        var (rule, level) = match.Value;

        _logger.LogInformation(
            "Error boundary matched (excluding {ExcludedActions}). Level: {Level}, Action: {Action}, Priority: {Priority}, Code: {ErrorCode}",
            string.Join(", ", excludeActions),
            level,
            rule.Rule.Action,
            rule.Rule.Priority,
            error.Code);

        return BoundaryResolutionResult.Handled(rule, level);
    }
}

