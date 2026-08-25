using Microsoft.Extensions.Options;

namespace BBT.Workflow.Security;

/// <summary>
/// Reads key material straight from <see cref="DataEncryptionOptions.Keys"/>.
/// <para>
/// For development and tests. Configuration is captured by diagnostic dumps and checked-in
/// appsettings files, which is exactly the exposure encryption exists to remove — so production
/// deployments use the Dapr secret store provider instead.
/// </para>
/// </summary>
/// <param name="options">Encryption options carrying the inline key map.</param>
public sealed class ConfigurationDataEncryptionKeyProvider(IOptions<DataEncryptionOptions> options)
    : IDataEncryptionKeyProvider
{
    private readonly Dictionary<string, DataEncryptionKey> _keys = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _keys.Clear();
        foreach (var (keyId, encoded) in options.Value.Keys)
        {
            _keys[keyId] = new DataEncryptionKey(keyId, DecodeKey(keyId, encoded));
        }

        return Task.CompletedTask;
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

    /// <summary>
    /// Decodes and length-checks a base64 key. A wrong-length key must fail at load, not at the
    /// first write, so a misconfiguration surfaces on startup rather than mid-transition.
    /// </summary>
    internal static byte[] DecodeKey(string keyId, string encoded)
    {
        byte[] material;
        try
        {
            material = Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            throw new SensitiveDataEncryptionException(
                $"Encryption key '{keyId}' is not valid base64.", ex);
        }

        if (material.Length != DataEncryptionKey.RequiredKeyLength)
        {
            throw new SensitiveDataEncryptionException(
                $"Encryption key '{keyId}' must be {DataEncryptionKey.RequiredKeyLength} bytes " +
                $"(AES-256) but was {material.Length}.");
        }

        return material;
    }
}
