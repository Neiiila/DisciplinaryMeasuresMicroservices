using Microsoft.EntityFrameworkCore;
using Notifications.Api.Domain;

namespace Notifications.Api.Infrastructure;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var notification = modelBuilder.Entity<Notification>();

        notification.ToTable("Notifications");
        notification.HasKey(n => n.Id);
        notification.Property(n => n.UserId).HasMaxLength(32).IsRequired();
        notification.Property(n => n.Message).HasMaxLength(1024).IsRequired();

        // Serves the only read query: a user's feed, newest first.
        notification.HasIndex(n => new { n.UserId, n.RaisedOn });

        // The idempotency guarantee: a redelivered event cannot produce a second
        // notification for the same recipient.
        notification.HasIndex(n => new { n.SourceEventId, n.UserId }).IsUnique();
    }
}
