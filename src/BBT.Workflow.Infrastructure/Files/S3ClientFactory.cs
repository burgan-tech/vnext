using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Files;

/// <summary>Creates the S3 client of an <c>x-storage</c> component; a seam so the store is testable without a server.</summary>
public interface IS3ClientFactory
{
    /// <summary>The (cached) client for <paramref name="component"/>, configured from <c>FileStorage:Stores</c>.</summary>
    IAmazonS3 Get(string component, FileStoreOptions store);
}

/// <summary>
/// One client per component, created on first use and kept for the process. Checksums are sent and validated only
/// when the operation requires them (<c>WHEN_REQUIRED</c>): the SDK's default CRC32 trailer is exactly the
/// aws-chunked framing that S3-compatible servers without trailer support store as the object (#145).
/// </summary>
public sealed class S3ClientFactory : IS3ClientFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<IAmazonS3>> _clients = new(StringComparer.Ordinal);

    public IAmazonS3 Get(string component, FileStoreOptions store)
        => _clients.GetOrAdd(component, _ => new Lazy<IAmazonS3>(() => Create(store))).Value;

    internal static AmazonS3Config ConfigOf(FileStoreOptions store)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = store.ForcePathStyle,
            AuthenticationRegion = store.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(store.ServiceUrl))
            config.ServiceURL = store.ServiceUrl;
        else
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(store.Region);

        if (!string.IsNullOrWhiteSpace(store.CaCertificatePath))
            config.HttpClientFactory = new TrustedRootHttpClientFactory(LoadRoots(store.CaCertificatePath));
        return config;
    }

    private static IAmazonS3 Create(FileStoreOptions store)
    {
        var config = ConfigOf(store);
        return string.IsNullOrWhiteSpace(store.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(store.AccessKey, store.SecretKey ?? string.Empty), config);
    }

    private static X509Certificate2Collection LoadRoots(string path)
    {
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(path);
        if (roots.Count == 0)
            throw new InvalidOperationException($"No certificate in {path}.");
        return roots;
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values.Where(c => c.IsValueCreated))
            client.Value.Dispose();
    }

    /// <summary>
    /// Validates the store's certificate chain against the configured roots only (custom root trust). A host name
    /// mismatch or a missing certificate is never accepted, whatever the roots say.
    /// </summary>
    private sealed class TrustedRootHttpClientFactory(X509Certificate2Collection roots) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate },
            };
            return new HttpClient(handler) { Timeout = clientConfig.Timeout ?? TimeSpan.FromSeconds(100) };
        }

        private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
                return true;
            if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
                return false;

            using var custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            custom.ChainPolicy.CustomTrustStore.AddRange(roots);
            return custom.Build(new X509Certificate2(certificate));
        }
    }
}
