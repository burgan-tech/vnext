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

// Parse the command before wiring anything: an invalid invocation must fail fast with usage text,
// not run a forward migration by accident. No arguments (and no DbMigrator:Command configuration)
// keeps the historical behavior: migrate everything forward at startup.
MigratorCommand command;
try
{
    command = MigratorCommand.Parse(args, configuration);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

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
    .AddTelemetry(configuration, verifyActivitySources: false)
    .AddDistributedLock(configuration)
    .AddSingleton(command)
    .AddSingleton<SchemaMigrationRunner>()
    .AddSingleton<SchemaDowngradeRunner>()
    .AddHostedService<SchemaMigrationHostedService>();

var host = builder.Build();

host.EnsureDatabaseCreatedInDevelopment();
host.Services.MigrateMessagingDbContext();

await host.RunAsync();

return Environment.ExitCode;