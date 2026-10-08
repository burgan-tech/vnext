using BBT.Aether.Results;
using BBT.Workflow.Files;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Service-to-service x-storage file read in any domain: in process when the domain is this runtime's,
/// otherwise over the owning domain's internal file endpoint. No authorization on either path.
/// </summary>
public interface IInstanceFileGateway
{
    /// <summary>Reads <paramref name="file"/> as referenced by the instance's latest data.</summary>
    Task<Result<InstanceFileContent>> ReadAsync(
        string domain, string flow, string instance, string file, CancellationToken cancellationToken);
}

/// <summary>Keyed-DI names of the two <see cref="IInstanceFileGateway"/> halves behind the router.</summary>
public static class InstanceFileGatewayKeys
{
    /// <summary>Key of the same-domain gateway.</summary>
    public const string Local = "instance-file-gateway-local";

    /// <summary>Key of the cross-domain gateway.</summary>
    public const string Remote = "instance-file-gateway-remote";
}
