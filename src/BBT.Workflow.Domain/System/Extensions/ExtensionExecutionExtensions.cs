using BBT.Workflow.Definitions;

namespace System.Extensions;

/// <summary>
/// Extension methods for Extension execution logic
/// </summary>
public static class ExtensionExecutionExtensions
{
    /// <summary>
    /// Determines if an extension should be executed based on its type, requested extensions, and current scope.
    /// </summary>
    /// <param name="extension">The extension to evaluate</param>
    /// <param name="extensionRequested">Set of specifically requested extension keys, or null to indicate no filtering preference</param>
    /// <param name="currentScope">Current execution scope (GetInstance, GetInstances, or Everywhere)</param>
    /// <returns>True if the extension should be executed, false otherwise</returns>
    public static bool ShouldExecute(this Extension extension, HashSet<string>? extensionRequested, ExtensionScope currentScope)
    {
        // First check if extension scope matches current execution context
        var scopeMatches = extension.Scope switch
        {
            ExtensionScope.GetInstance => currentScope == ExtensionScope.GetInstance || currentScope == ExtensionScope.Everywhere,
            ExtensionScope.GetAllInstances => currentScope == ExtensionScope.GetAllInstances || currentScope == ExtensionScope.Everywhere,
            ExtensionScope.Everywhere => true,
            _ => false
        };

        if (!scopeMatches)
            return false;

        // Then check extension type and request filtering logic
        return extension.Type switch
        {
            // Core extensions always execute regardless of extensionRequested (when scope matches)
            ExtensionType.Global => true,
            ExtensionType.GlobalAndRequested => extensionRequested?.Contains(extension.Key) == true,
            
            // Workflow-specific extensions that respect extensionRequested parameter
            ExtensionType.DefinedFlows => extensionRequested == null || extensionRequested.Contains(extension.Key),
            ExtensionType.DefinedFlowAndRequested => extensionRequested?.Contains(extension.Key) == true,
            
            _ => false
        };
    }
} 