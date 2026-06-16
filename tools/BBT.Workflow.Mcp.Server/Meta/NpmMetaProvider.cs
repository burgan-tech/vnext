using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Nodes;
using BBT.Workflow.Mcp.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Mcp.Meta;

/// <summary>
/// Loads the offline <c>vnext-meta</c> JSON files from the public npm registry
/// (<c>https://registry.npmjs.org</c>) at startup and holds them in memory. The pinned package
/// version comes from <see cref="McpOptions.MetaPackageVersion"/>. Failures are swallowed (logged)
/// so the host still starts and the live component/runtime tools keep working.
/// </summary>
public sealed class NpmMetaProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<McpOptions> options,
    ILogger<NpmMetaProvider> logger) : IMetaProvider
{
    private const string Registry = "https://registry.npmjs.org";

    private readonly McpOptions _options = options.Value;
    private volatile IReadOnlyDictionary<string, JsonNode>? _files;

    /// <inheritdoc />
    public bool IsLoaded => _files is not null;

    /// <inheritdoc />
    public JsonNode? Get(string fileName) =>
        _files is not null && _files.TryGetValue(fileName, out var node) ? node : null;

    /// <inheritdoc />
    public async Task<bool> LoadAsync(CancellationToken cancellationToken = default)
    {
        var name = _options.MetaPackageName;
        var version = _options.MetaPackageVersion;

        try
        {
            using var http = httpClientFactory.CreateClient("npm");

            // 1) Resolve the version document to get the tarball URL.
            var manifestUrl = $"{Registry}/{name}/{version}";
            var manifest = await http.GetFromJsonNodeAsync(manifestUrl, cancellationToken);
            var tarballUrl = manifest?["dist"]?["tarball"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tarballUrl))
            {
                logger.LogWarning("vnext-meta npm manifest {Name}@{Version} has no dist.tarball.", name, version);
                return false;
            }

            // 2) Download + extract the .tgz (npm roots all files under "package/").
            await using var tarball = await http.GetStreamAsync(tarballUrl, cancellationToken);
            await using var gzip = new GZipStream(tarball, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);

            var files = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
            while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    continue;
                if (!entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileName = Path.GetFileName(entry.Name);
                if (entry.DataStream is null)
                    continue;

                using var reader = new StreamReader(entry.DataStream);
                var content = await reader.ReadToEndAsync(cancellationToken);
                var parsed = TryParse(content);
                if (parsed is not null)
                    files[fileName] = parsed;
            }

            _files = files;
            logger.LogInformation("Loaded {Count} vnext-meta files from {Name}@{Version}.", files.Count, name, version);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load vnext-meta {Name}@{Version} from npm; meta tools will be degraded.", name, version);
            return false;
        }
    }

    private static JsonNode? TryParse(string content)
    {
        try
        {
            return JsonNode.Parse(content);
        }
        catch
        {
            return null;
        }
    }
}

internal static class NpmHttpExtensions
{
    public static async Task<JsonNode?> GetFromJsonNodeAsync(this HttpClient http, string url, CancellationToken cancellationToken)
    {
        var json = await http.GetStringAsync(url, cancellationToken);
        return JsonNode.Parse(json);
    }
}
