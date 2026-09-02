using System.Buffers.Binary;
using System.Formats.Tar;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Execution.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Python.Containers;

/// <summary>
/// Minimal Docker Engine API client for ephemeral Python runner containers.
/// It deliberately avoids Docker CLI and keeps the driver boundary replaceable.
/// </summary>
public sealed class DockerContainerExecutionDriver : IContainerExecutionDriver, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PythonContainerOptions _options;
    private readonly ILogger<DockerContainerExecutionDriver> _logger;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _imagePullLock = new(1, 1);

    public DockerContainerExecutionDriver(
        IOptions<PythonOptions> options,
        ILogger<DockerContainerExecutionDriver> logger)
        : this(options.Value.Container, CreateClient(options.Value.Container), logger)
    {
    }

    internal DockerContainerExecutionDriver(
        PythonContainerOptions options,
        HttpClient client,
        ILogger<DockerContainerExecutionDriver> logger)
    {
        _options = options;
        _logger = logger;
        _client = client;
    }

    public string Name => "docker";

    public async Task<ContainerExecutionResult> ExecuteAsync(
        ContainerExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        await EnsureImageAsync(request.Image, timeout.Token);
        var containerId = await CreateContainerAsync(request, timeout.Token);

        try
        {
            await UploadInputAsync(containerId, request.InputJson, timeout.Token);
            await SendSuccessAsync(HttpMethod.Post, Api($"containers/{containerId}/start"), null, timeout.Token);

            DockerWaitResponse waitResponse;
            try
            {
                waitResponse = await WaitAsync(containerId, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                await TryKillAsync(containerId);
                throw;
            }

            var (stdout, stderr) = await ReadLogsAsync(
                containerId,
                request.MaxResponseBytes,
                timeout.Token);

            return new ContainerExecutionResult(waitResponse.StatusCode, stdout, stderr);
        }
        finally
        {
            await TryStopAsync(containerId);
            await TryRemoveAsync(containerId);
        }
    }

    public async Task CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync("/_ping", cancellationToken);
        await EnsureSuccessAsync(response, "Docker Engine ping failed", cancellationToken);
        await EnsureImageAsync(_options.Image, cancellationToken);
    }

    public void Dispose()
    {
        _client.Dispose();
        _imagePullLock.Dispose();
    }

    private async Task<string> CreateContainerAsync(
        ContainerExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var name = $"vnext-python-{Guid.NewGuid():N}";
        var environment = request.Environment.Select(pair => $"{pair.Key}={pair.Value}").ToArray();
        var payload = new
        {
            Image = request.Image,
            Cmd = new[] { "--input", "/opt/vnext-python/request.json" },
            Env = environment,
            User = "65532:65532",
            AttachStdout = true,
            AttachStderr = true,
            Tty = false,
            NetworkDisabled = string.Equals(request.NetworkMode, "none", StringComparison.OrdinalIgnoreCase),
            HostConfig = new
            {
                Memory = request.MemoryBytes,
                NanoCpus = request.NanoCpus,
                PidsLimit = request.PidsLimit,
                ReadonlyRootfs = true,
                NetworkMode = request.NetworkMode,
                CapDrop = new[] { "ALL" },
                SecurityOpt = new[] { "no-new-privileges:true" },
                Tmpfs = new Dictionary<string, string>
                {
                    ["/tmp"] = $"rw,noexec,nosuid,nodev,size={request.TmpfsBytes}"
                }
            }
        };

        using var content = JsonContent(payload);
        using var response = await _client.PostAsync(
            Api($"containers/create?name={Uri.EscapeDataString(name)}"),
            content,
            cancellationToken);
        await EnsureSuccessAsync(response, "Docker container creation failed", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var created = JsonSerializer.Deserialize<DockerCreateResponse>(body, JsonOptions);
        return !string.IsNullOrWhiteSpace(created?.Id)
            ? created.Id
            : throw new PythonExecutionException(
                "Docker Engine did not return a container id.",
                "container_create_failed");
    }

    private async Task UploadInputAsync(
        string containerId,
        string inputJson,
        CancellationToken cancellationToken)
    {
        await using var archive = new MemoryStream();
        await using (var writer = new TarWriter(archive, leaveOpen: true))
        {
            var data = new MemoryStream(Encoding.UTF8.GetBytes(inputJson), writable: false);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "request.json")
            {
                DataStream = data,
                Mode = UnixFileMode.UserRead,
                Uid = 65532,
                Gid = 65532
            };
            await writer.WriteEntryAsync(entry, cancellationToken);
        }

        archive.Position = 0;
        using var content = new StreamContent(archive);
        content.Headers.ContentType = new("application/x-tar");
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            Api($"containers/{containerId}/archive?path={Uri.EscapeDataString("/opt/vnext-python")}"))
        {
            Content = content
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Uploading the Python request failed", cancellationToken);
    }

    private async Task<DockerWaitResponse> WaitAsync(
        string containerId,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(string.Empty);
        using var response = await _client.PostAsync(
            Api($"containers/{containerId}/wait?condition=not-running"),
            content,
            cancellationToken);
        await EnsureSuccessAsync(response, "Waiting for the Python container failed", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<DockerWaitResponse>(body, JsonOptions)
               ?? throw new PythonExecutionException(
                   "Docker Engine returned an invalid wait response.",
                   "container_failed");
    }

    private async Task<(string Stdout, string Stderr)> ReadLogsAsync(
        string containerId,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
            Api($"containers/{containerId}/logs?stdout=1&stderr=1"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "Reading Python container logs failed", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var header = new byte[8];
        var totalBytes = 0;

        while (await ReadExactlyOrEofAsync(stream, header, cancellationToken))
        {
            var frameLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));
            if (frameLength < 0 || totalBytes + frameLength > maxResponseBytes)
            {
                throw new PythonExecutionException(
                    "Python container response exceeds the configured size limit.",
                    "output_limit_exceeded");
            }

            var frame = new byte[frameLength];
            await stream.ReadExactlyAsync(frame, cancellationToken);
            totalBytes += frameLength;
            var destination = header[0] == 2 ? stderr : stdout;
            await destination.WriteAsync(frame, cancellationToken);
        }

        return (Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
    }

    private async Task EnsureImageAsync(string image, CancellationToken cancellationToken)
    {
        using (var inspect = await _client.GetAsync(
                   Api($"images/{Uri.EscapeDataString(image)}/json"),
                   cancellationToken))
        {
            if (inspect.IsSuccessStatusCode)
            {
                return;
            }

            if ((int)inspect.StatusCode != 404)
            {
                await EnsureSuccessAsync(inspect, "Docker image inspection failed", cancellationToken);
            }
        }

        if (string.Equals(_options.PullPolicy, "never", StringComparison.OrdinalIgnoreCase))
        {
            throw new PythonExecutionException(
                $"Python runner image '{image}' is not present and pull policy is 'never'.",
                "container_image_unavailable");
        }

        await _imagePullLock.WaitAsync(cancellationToken);
        try
        {
            using var recheck = await _client.GetAsync(
                Api($"images/{Uri.EscapeDataString(image)}/json"),
                cancellationToken);
            if (recheck.IsSuccessStatusCode)
            {
                return;
            }

            using var content = new StringContent(string.Empty);
            using var pull = await _client.PostAsync(
                Api($"images/create?fromImage={Uri.EscapeDataString(image)}"),
                content,
                cancellationToken);
            await EnsureSuccessAsync(pull, $"Pulling Python runner image '{image}' failed", cancellationToken);
            var pullBody = await pull.Content.ReadAsStringAsync(cancellationToken);
            if (pullBody.Contains("\"error\"", StringComparison.OrdinalIgnoreCase))
            {
                throw new PythonExecutionException(
                    $"Docker Engine could not pull Python runner image '{image}'.",
                    "container_image_unavailable");
            }
        }
        finally
        {
            _imagePullLock.Release();
        }
    }

    private async Task TryKillAsync(string containerId)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var content = new StringContent(string.Empty);
            using var response = await _client.PostAsync(
                Api($"containers/{containerId}/kill?signal=KILL"),
                content,
                cleanup.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to kill Python container {ContainerId}", containerId);
        }
    }

    private async Task TryRemoveAsync(string containerId)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await _client.DeleteAsync(
                Api($"containers/{containerId}?force=1&v=1"),
                cleanup.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to remove Python container {ContainerId}", containerId);
        }
    }

    private async Task TryStopAsync(string containerId)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var content = new StringContent(string.Empty);
            using var response = await _client.PostAsync(
                Api($"containers/{containerId}/stop?t=2"),
                content,
                cleanup.Token);
            if (!response.IsSuccessStatusCode &&
                response.StatusCode is not System.Net.HttpStatusCode.NotModified and
                    not System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug(
                    "Docker Engine returned {StatusCode} while stopping Python container {ContainerId}",
                    (int)response.StatusCode,
                    containerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to stop Python container {ContainerId}", containerId);
        }
    }

    private async Task SendSuccessAsync(
        HttpMethod method,
        string uri,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Docker Engine request failed", cancellationToken);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string message,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new PythonExecutionException(
            $"{message} ({(int)response.StatusCode}): {body}",
            "container_driver_error");
    }

    private static async Task<bool> ReadExactlyOrEofAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("Docker log stream ended in the middle of a frame header.");
            }

            offset += read;
        }

        return true;
    }

    private string Api(string path) => $"/{_options.ApiVersion.Trim('/')}/{path}";

    private static StringContent JsonContent(object value) => new(
        JsonSerializer.Serialize(value, JsonOptions),
        Encoding.UTF8,
        "application/json");

    private static HttpClient CreateClient(PythonContainerOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        Uri baseAddress;
        if (options.Endpoint.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
        {
            var socketPath = options.Endpoint["unix://".Length..];
            handler.ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
            baseAddress = new Uri("http://docker");
        }
        else
        {
            baseAddress = new Uri(options.Endpoint, UriKind.Absolute);
            ConfigureTls(handler, options);
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static void ConfigureTls(SocketsHttpHandler handler, PythonContainerOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ClientCertificatePath))
        {
            handler.SslOptions.ClientCertificates ??= [];
            handler.SslOptions.ClientCertificates.Add(X509CertificateLoader.LoadPkcs12FromFile(
                options.ClientCertificatePath,
                options.ClientCertificatePassword));
        }

        if (string.IsNullOrWhiteSpace(options.CaCertificatePath))
        {
            return;
        }

        var root = X509CertificateLoader.LoadCertificateFromFile(options.CaCertificatePath);
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null)
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(new X509Certificate2(certificate)) &&
                   errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors;
        };
    }

    private sealed class DockerCreateResponse
    {
        public string Id { get; init; } = string.Empty;
    }

    private sealed class DockerWaitResponse
    {
        public int StatusCode { get; init; }
    }
}
