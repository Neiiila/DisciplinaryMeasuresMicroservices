using Identity.Api.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Identity.Api.Infrastructure;

/// <summary>
/// Identity's own database.
/// </summary>
/// <remarks>
/// Database-per-service: nothing outside this service reads these tables. Other
/// services that need directory data receive it as integration events and keep
/// their own projection, which is what allows this schema to change without
/// coordinating a release across the platform.
/// </remarks>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The outbox stores pending messages in this service's own database, so
        // its tables have to be part of this schema — that shared transaction is
        // the whole point of the pattern.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        var user = modelBuilder.Entity<User>();

        user.ToTable("Users");
        user.HasKey(u => u.Id);
        user.Property(u => u.Id).HasMaxLength(32);

        user.OwnsOne(u => u.Name, name =>
        {
            name.Property(n => n.First).HasColumnName("FirstName").HasMaxLength(100).IsRequired();
            name.Property(n => n.Last).HasColumnName("LastName").HasMaxLength(100).IsRequired();
        });

        user.OwnsOne(u => u.Employment, employment =>
        {
            employment.Property(e => e.HiringDate).HasColumnName("HiringDate");
            employment.Property(e => e.Status).HasColumnName("EmploymentStatus").HasMaxLength(64);
            employment.Property(e => e.ContractType).HasColumnName("ContractType").HasMaxLength(64);
            employment.Property(e => e.Position).HasColumnName("Position").HasMaxLength(128);
            employment.Property(e => e.LocalJobTitle).HasColumnName("LocalJobTitle").HasMaxLength(128);
            employment.Property(e => e.SiteCode).HasColumnName("SiteCode").HasMaxLength(32);
            employment.Property(e => e.Site).HasColumnName("Site").HasMaxLength(128);
            employment.Property(e => e.Department).HasColumnName("Department").HasMaxLength(128);
            employment.Property(e => e.BusinessUnit).HasColumnName("BusinessUnit").HasMaxLength(128);
            employment.Property(e => e.Segment).HasColumnName("Segment").HasMaxLength(128);
        });

        user.Property(u => u.Email).HasMaxLength(256);
        user.Property(u => u.Cin).HasMaxLength(32);
        user.Property(u => u.PhoneNumber).HasMaxLength(32);
        user.Property(u => u.Gender).HasMaxLength(16);
        user.Property(u => u.PhotoPath).HasMaxLength(512);
        user.Property(u => u.PasswordHash).HasMaxLength(512);

        // Enums are stored as strings so a row is readable, and so inserting a new
        // member in the middle of the enum cannot silently re-label existing rows.
        user.Property(u => u.Role).HasConversion<string>().HasMaxLength(32);
        user.Property(u => u.AccountStatus).HasConversion<string>().HasMaxLength(32);

        // Filtered, so the many users with no email do not collide on NULL.
        user.HasIndex(u => u.Email).IsUnique().HasFilter("[Email] IS NOT NULL");

        user.HasOne(u => u.Supervisor)
            .WithMany()
            .HasForeignKey(u => u.SupervisorId)
            .OnDelete(DeleteBehavior.Restrict);

        // Soft-deleted users disappear from every query unless it opts out with
        // IgnoreQueryFilters, so no listing has to remember to filter them.
        user.HasQueryFilter(u => !u.IsDeleted);
    }
}
