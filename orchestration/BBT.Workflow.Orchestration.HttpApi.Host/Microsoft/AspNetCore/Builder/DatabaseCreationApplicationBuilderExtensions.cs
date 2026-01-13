using BBT.Aether.Threading;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods for ensuring the database exists in Development environment.
/// </summary>
public static class DatabaseCreationApplicationBuilderExtensions
{
    /// <summary>
    /// Ensures that the database exists when running in Development environment.
    /// Does NOT run migrations.
    /// </summary>
    /// <param name="app">The web application.</param>
    public static void EnsureDatabaseCreatedInDevelopment(this WebApplication app)
    {
#if DEBUG
        // Only run in Development environment (Debug/Local)
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        AsyncHelper.RunSync(async () =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            
            // Handle WorkflowDbContext
            var workflowDbContext = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            if (workflowDbContext.Database.IsRelational())
            {
                // EnsureCreatedAsync creates the database if it doesn't exist.
                // It does NOT apply migrations if the database already exists.
                // If the database does not exist, it creates it with the current model schema 
                // (effectively bypassing migrations for the initial creation).
                // Note: EnsureCreatedAsync returns true if the database was created, false if it already existed.
                await workflowDbContext.Database.EnsureCreatedAsync();
            }

            // Handle MessagingDbContext
            var messagingDbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            if (messagingDbContext.Database.IsRelational())
            {
                await messagingDbContext.Database.EnsureCreatedAsync();
            }
        });
#endif
    }
}
