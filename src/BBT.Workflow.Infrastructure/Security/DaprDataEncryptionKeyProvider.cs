using BBT.Workflow.Security;
using Dapr.Client;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Infrastructure.Security;

/// <summary>
/// Loads instance-data encryption keys from the Dapr secret store (HashiCorp Vault in the shipped
/// compose files).
/// <para>
/// The store is configured with <c>vaultValueType: map</c>, so a single fetch returns every key id
/// at once — active and retired. That is what allows decryption to stay synchronous: by the time a
/// row is read, the key that wrote it is already in memory.
/// </para>
/// </summary>
/// <param name="daprClient">Dapr client used to read the secret map.</param>
/// <param name="options">Encryption options naming the store, secret and key prefix.</param>
public sealed class DaprDataEncryptionKeyProvider(
    DaprClient daprClient,
    IOptions<DataEncryptionOptions> options) : IDataEncryptionKeyProvider
{
    private volatile Dictionary<string, DataEncryptionKey> _keys = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var secrets = await daprClient.GetSecretAsync(
            settings.SecretStoreName,
            settings.SecretName,
            cancellationToken: cancellationToken);

        var loaded = new Dictionary<string, DataEncryptionKey>(StringComparer.Ordinal);
        foreach (var (name, value) in secrets)
        {
            if (!name.StartsWith(settings.SecretKeyPrefix, StringComparison.Ordinal))
                continue;

            var keyId = name[settings.SecretKeyPrefix.Length..];
            if (string.IsNullOrWhiteSpace(keyId))
                continue;

            loaded[keyId] = new DataEncryptionKey(
                keyId,
                ConfigurationDataEncryptionKeyProvider.DecodeKey(keyId, value));
        }

        // Swapped atomically so a reload never exposes a half-populated map to a concurrent read.
        _keys = loaded;
    }

    /// <inheritdoc />
    public DataEncryptionKey? GetActive()
    {
        var activeKeyId = options.Value.ActiveKeyId;
        if (string.IsNullOrWhiteSpace(activeKeyId))
            return null;

        return _keys.TryGetValue(activeKeyId, out var key) ? key : null;
    }

    /// <inheritdoc />
    public bool TryGet(string keyId, out DataEncryptionKey key) => _keys.TryGetValue(keyId, out key!);
}
