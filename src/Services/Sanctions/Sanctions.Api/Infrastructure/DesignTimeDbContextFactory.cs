using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sanctions.Api.Infrastructure;

/// <summary>
/// Builds a context for the EF tooling without starting the application.
/// </summary>
/// <remarks>
/// The host requires a signing key and a reachable broker at startup, neither of
/// which a design-time tool should need. The connection string here only selects
/// the provider and is never connected to: migrations are generated from the
/// model, not from the database.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SanctionsDbContext>
{
    private const string FallbackConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=DisciplinaryMeasures.Sanctions;Trusted_Connection=True;TrustServerCertificate=True";

    public SanctionsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SanctionsDb")
            ?? FallbackConnectionString;

        var options = new DbContextOptionsBuilder<SanctionsDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new SanctionsDbContext(options);
    }
}
