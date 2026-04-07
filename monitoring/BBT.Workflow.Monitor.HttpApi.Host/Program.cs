using BBT.Aether.AspNetCore.Dapr;
using BBT.Aether.AspNetCore.Threads;
using Dapr.Client;
using Dapr.Extensions.Configuration;

ThreadPoolHelper.ConfigureThreadPool();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.SetBasePath(Directory.GetCurrentDirectory());

if (builder.Configuration.GetValue<bool>("Vault:Enabled", false))
{
    var daprClient = new DaprClientBuilder().Build();
    await DaprCheckForSidecarHelper.CheckAsync(daprClient);
    builder.Configuration.AddDaprSecretStore(
        builder.Configuration["DAPR_SECRET_STORE_NAME"] ?? "vnext-secret", daprClient);
}

builder.WebHost.ConfigureKestrel(option => option.AddServerHeader = false);

builder.Services.AddMonitorApiModule();

var app = builder.Build();
app.UseMonitorApiModule();
await app.RunAsync();
