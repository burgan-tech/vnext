using BBT.Workflow.Mcp.Configuration;

// Transport selection: `--transport http` (Streamable HTTP, hosted/remote) or stdio (default, local IDE).
var transport = ResolveTransport(args);

if (transport == "http")
{
    var builder = WebApplication.CreateBuilder(args);

    var options = builder.Services.AddVNextMcpOptions(builder.Configuration);
    builder.Services.AddOrchestrationClient(options);
    builder.Services.AddMetaProvider();
    builder.Services
        .AddMcpServer(ConfigureServerInfo)
        .WithHttpTransport()
        .AddVNextTools(options);

    var app = builder.Build();
    app.MapMcp();
    await app.RunAsync();
}
else
{
    var builder = Host.CreateApplicationBuilder(args);

    // stdio carries the MCP protocol on stdout — all logs must go to stderr or they corrupt it.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(consoleOptions => consoleOptions.LogToStandardErrorThreshold = LogLevel.Trace);

    var options = builder.Services.AddVNextMcpOptions(builder.Configuration);
    builder.Services.AddOrchestrationClient(options);
    builder.Services.AddMetaProvider();
    builder.Services
        .AddMcpServer(ConfigureServerInfo)
        .WithStdioServerTransport()
        .AddVNextTools(options);

    await builder.Build().RunAsync();
}

return;

static string ResolveTransport(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--transport", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            return args[i + 1].ToLowerInvariant();

        if (args[i].StartsWith("--transport=", StringComparison.OrdinalIgnoreCase))
            return args[i]["--transport=".Length..].ToLowerInvariant();
    }

    return "stdio";
}

static void ConfigureServerInfo(ModelContextProtocol.Server.McpServerOptions serverOptions)
{
    serverOptions.ServerInfo = new ModelContextProtocol.Protocol.Implementation
    {
        Name = "vnext-runtime",
        Version = ThisAssembly.Version
    };
}

internal static class ThisAssembly
{
    public static string Version =>
        typeof(ThisAssembly).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
