using Microsoft.EntityFrameworkCore;
using Sanctions.Api.Domain;

namespace Sanctions.Api.Infrastructure;

/// <summary>
/// Sanctions' own database.
/// </summary>
/// <remarks>
/// It holds the requests, the fault catalogue, and a projection of the employee
/// directory kept current from Identity's events. There is no foreign key from a
/// request to an employee: they live in different databases, so referential
/// integrity across that boundary is the projection's job, not the schema's.
/// </remarks>
public sealed class SanctionsDbContext(DbContextOptions<SanctionsDbContext> options) : DbContext(options)
{
    public DbSet<SanctionRequest> SanctionRequests => Set<SanctionRequest>();

    public DbSet<Fault> Faults => Set<Fault>();

    public DbSet<EmployeeProjection> Employees => Set<EmployeeProjection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var request = modelBuilder.Entity<SanctionRequest>();

        request.ToTable("SanctionRequests");
        request.HasKey(r => r.Id);
        request.Property(r => r.Description).HasMaxLength(1024).IsRequired();
        request.Property(r => r.Details).HasMaxLength(4096);
        request.Property(r => r.EmployeeId).HasMaxLength(32).IsRequired();
        request.Property(r => r.RequesterId).HasMaxLength(32).IsRequired();
        request.Property(r => r.CurrentValidatorId).HasMaxLength(32);
        request.Property(r => r.AttachmentPath).HasMaxLength(512);

        // Indexed because these are the three list queries the API serves.
        request.HasIndex(r => r.RequesterId);
        request.HasIndex(r => r.CurrentValidatorId);
        request.HasIndex(r => r.EmployeeId);

        request.HasOne(r => r.Fault)
            .WithMany()
            .HasForeignKey(r => r.FaultId)
            .OnDelete(DeleteBehavior.Restrict);

        request.HasMany(r => r.Validations)
            .WithOne()
            .HasForeignKey(v => v.SanctionRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        request.Navigation(r => r.Validations).UsePropertyAccessMode(PropertyAccessMode.Field);

        var validation = modelBuilder.Entity<RequestValidation>();
        validation.ToTable("RequestValidations");
        validation.HasKey(v => v.Id);
        validation.Property(v => v.ValidatorId).HasMaxLength(32).IsRequired();
        validation.Property(v => v.Note).HasMaxLength(2048);
        validation.Property(v => v.Decision).HasConversion<string>().HasMaxLength(32);

        // One answer per validator per request, enforced by the database as well as
        // by the aggregate: a concurrent double submit would otherwise slip past the
        // in-memory check.
        validation.HasIndex(v => new { v.SanctionRequestId, v.ValidatorId }).IsUnique();

        var fault = modelBuilder.Entity<Fault>();
        fault.ToTable("Faults");
        fault.HasKey(f => f.Id);
        fault.Property(f => f.Title).HasMaxLength(256).IsRequired();
        fault.Property(f => f.Category).HasMaxLength(128).IsRequired();

        var employee = modelBuilder.Entity<EmployeeProjection>();
        employee.ToTable("EmployeeProjections");
        employee.HasKey(e => e.Id);
        employee.Property(e => e.Id).HasMaxLength(32);
        employee.Property(e => e.FullName).HasMaxLength(256).IsRequired();
        employee.Property(e => e.Email).HasMaxLength(256);
        employee.Property(e => e.SupervisorId).HasMaxLength(32);
        employee.Property(e => e.Department).HasMaxLength(128);
        employee.Property(e => e.Position).HasMaxLength(128);
    }
}
