using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Notifications.Api.Infrastructure;

/// <summary>
/// Builds a context for the EF tooling without starting the application.
/// </summary>
/// <remarks>
/// The host requires a signing key and a reachable broker at startup, neither of
/// which a design-time tool should need. The connection string here only selects
/// the provider and is never connected to: migrations are generated from the
/// model, not from the database.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    private const string FallbackConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=DisciplinaryMeasures.Notifications;Trusted_Connection=True;TrustServerCertificate=True";

    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__NotificationsDb")
            ?? FallbackConnectionString;

        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new NotificationsDbContext(options);
    }
}
