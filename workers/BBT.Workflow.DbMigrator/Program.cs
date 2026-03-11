using BBT.Aether.AspNetCore.Dapr;
using BBT.Workflow.DbMigrator;
using Dapr.Client;
using Dapr.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.SetBasePath(Directory.GetCurrentDirectory());
builder.Configuration.AddEnvironmentVariables();

if (builder.Configuration.GetValue<bool>("Vault:Enabled", false))
{
    var daprClient = new DaprClientBuilder().Build();
    await DaprCheckForSidecarHelper.CheckAsync(daprClient);
    builder.Configuration.AddDaprSecretStore(
        builder.Configuration["DAPR_SECRET_STORE_NAME"] ?? "vnext-secret",
        daprClient);
}

var configuration = builder.Configuration;

builder.Services
    .AddAetherCore(options =>
    {
        options.Environment ??= Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
        options.ApplicationName ??= configuration.GetValue<string?>("ApplicationName") ?? "vnext-db-migrator";
    })
    .AddAetherAmbientServiceProvider()
    .AddJsonSerializerOptions()
    .AddDaprClients()
    .AddDomainModule()
    .AddInfrastructureModule(configuration)
    .AddDbContext(configuration)
    .AddTelemetry(configuration)
    .AddDistributedLock(configuration)
    .AddRedis()
    .AddSingleton<SchemaMigrationRunner>();

var host = builder.Build();

host.EnsureDatabaseCreatedInDevelopment();
host.Services.MigrateMessagingDbContext();

using var scope = host.Services.CreateScope();
var runner = scope.ServiceProvider.GetRequiredService<SchemaMigrationRunner>();

// RunAsync returns only after all migrations (system + all parallel domain schemas) have fully completed.
// Process must not exit before this completes to avoid cutting migrations short.
await runner.RunAsync(CancellationToken.None);

// All migrations are guaranteed complete at this point. Allow OTLP log exporter to flush before process exit.
var flushDelaySeconds = builder.Configuration.GetValue("DbMigrator:LogFlushDelaySeconds", 2);
if (flushDelaySeconds > 0)
    await Task.Delay(TimeSpan.FromSeconds(flushDelaySeconds), CancellationToken.None);

return runner.Success ? 0 : 1;