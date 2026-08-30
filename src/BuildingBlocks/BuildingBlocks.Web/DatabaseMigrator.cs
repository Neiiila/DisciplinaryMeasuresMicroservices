using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Web;

public static class DatabaseMigrator
{
    /// <summary>
    /// Applies pending migrations at startup, in Development only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists so a fresh clone runs with one command. It is deliberately
    /// gated to Development: applying schema changes from application startup in
    /// production means every replica races to migrate the same database, and a
    /// rolling deploy would run the migration while the previous version is still
    /// serving traffic. Real environments should migrate as a separate,
    /// single-instance step before the new version starts.
    /// </para>
    /// <para>
    /// The retry loop is for the container case: SQL Server accepts TCP
    /// connections before it will accept logins, so the first attempt after
    /// compose reports the dependency healthy can still fail.
    /// </para>
    /// </remarks>
    public static async Task MigrateInDevelopmentAsync<TContext>(this WebApplication app)
        where TContext : DbContext
    {
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

        const int MaxAttempts = 10;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await context.Database.MigrateAsync();
                logger.LogInformation("{Context} schema is up to date.", typeof(TContext).Name);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(attempt * 2, 10));

                logger.LogWarning(
                    "Migrating {Context} failed on attempt {Attempt}/{Max} ({Reason}). Retrying in {Delay}s.",
                    typeof(TContext).Name, attempt, MaxAttempts, ex.Message, delay.TotalSeconds);

                await Task.Delay(delay);
            }
        }

        // Out of attempts: fail loudly rather than serve requests against a
        // database whose schema is unknown.
        await context.Database.MigrateAsync();
    }
}
