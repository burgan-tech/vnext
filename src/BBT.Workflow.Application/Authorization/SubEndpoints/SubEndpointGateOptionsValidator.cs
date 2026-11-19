using BBT.Workflow.Execution.RuntimeCaller;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Authorization.SubEndpoints;

/// <summary>
/// Fails startup on a sub-endpoint gate that could refuse the runtime's own calls or decide on
/// something it cannot evaluate: a mode other than Off needs a loaded runtime-caller key ring (or
/// every runtime call would be token-less) and an explicit, non-empty, evaluable grant set (the
/// shared rule reads an empty set as "allow everyone").
/// </summary>
internal sealed class SubEndpointGateOptionsValidator(IRuntimeCallerVerifier? verifier = null)
    : IValidateOptions<SubEndpointGateOptions>
{
    public ValidateOptionsResult Validate(string? name, SubEndpointGateOptions options)
    {
        var failures = new List<string>();
        SubEndpointGrants.Build(options.Grants, failures);

        if (options.IsEnabled)
        {
            if (options.Grants.Count == 0)
                failures.Add($"{SubEndpointGateOptions.SectionName}:Grants must be non-empty when the gate is on. " +
                             "Write runtime-only as [{ \"role\": \"$Everyone\", \"grant\": \"deny\" }].");

            if (verifier is not { IsConfigured: true })
                failures.Add($"{SubEndpointGateOptions.SectionName} is on but no runtime-caller key ring is loaded " +
                             "(Workflow:RuntimeCaller:KeyFile): every runtime call would arrive without a token.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
