using BBT.Workflow.Logging;
using BBT.Workflow.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Infrastructure.HostedServices;

/// <summary>
/// Loads encryption key material and installs the process-wide cipher used to decrypt instance
/// data on read.
/// <para>
/// Runs in <b>every</b> host that touches instance data, not only the ones that write it. A worker
/// that reads an encrypted instance without a configured cipher would hit
/// <see cref="NullSensitiveDataCipher"/> and fail — which is the correct failure, but only if the
/// host was genuinely meant to run without keys.
/// </para>
/// <para>
/// Startup fails loudly when encryption is enabled but keys cannot be loaded. Starting anyway would
/// mean writing plaintext into a column the operator believes is encrypted.
/// </para>
/// </summary>
/// <param name="keyProvider">Key source to load from.</param>
/// <param name="options">Encryption options.</param>
/// <param name="logger">Logger.</param>
public sealed class SensitiveDataCipherHostedService(
    IDataEncryptionKeyProvider keyProvider,
    IOptions<DataEncryptionOptions> options,
    ILogger<SensitiveDataCipherHostedService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;

        try
        {
            await keyProvider.LoadAsync(cancellationToken);
        }
        catch (Exception ex) when (!settings.Enabled)
        {
            // Encryption is off, so a missing key source is not an error — but it is worth saying
            // out loud, because it is the thing that will bite when someone flips Enabled on.
            logger.SensitiveDataEncryptionKeysUnavailable(ex.Message);
            SensitiveDataCipherAccessor.Configure(new SensitiveDataCipher(keyProvider, isEnabled: false));
            return;
        }

        if (settings.Enabled && keyProvider.GetActive() is null)
        {
            throw new SensitiveDataEncryptionException(
                $"Instance-data encryption is enabled but active key '{settings.ActiveKeyId}' was " +
                $"not found in the '{settings.KeySource}' key source. Refusing to start: writing " +
                "plaintext into a column the operator believes is encrypted is worse than failing.");
        }

        SensitiveDataCipherAccessor.Configure(new SensitiveDataCipher(keyProvider, settings.Enabled));
        logger.SensitiveDataEncryptionConfigured(settings.Enabled, settings.ActiveKeyId ?? "-");
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
