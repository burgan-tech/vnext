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
    .AddSingleton<SchemaMigrationRunner>()
    .AddHostedService<SchemaMigrationHostedService>();

var host = builder.Build();

host.EnsureDatabaseCreatedInDevelopment();
host.Services.MigrateMessagingDbContext();

await host.RunAsync();

return Environment.ExitCode;