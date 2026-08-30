using BuildingBlocks.Web;
using BuildingBlocks.Web.Authentication;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Notifications.Api.Consumers;
using Notifications.Api.Infrastructure;
using Notifications.Api.Realtime;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddServiceDefaults("notifications");
builder.Services.AddPlatformJwtAuthentication(builder.Configuration);

builder.Services.AddDbContext<NotificationsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("NotificationsDb")));

builder.Services.AddSignalR();
builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
builder.Services.AddScoped<INotificationPublisher, SignalRNotificationPublisher>();

builder.Services.AddMassTransit(bus =>
{
    bus.AddConsumer<SanctionAwaitingValidatorConsumer>();
    bus.AddConsumer<SanctionSettledConsumer>();
    bus.AddConsumer<AccountAwaitingActivationConsumer>();

    bus.SetKebabCaseEndpointNameFormatter();

    bus.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration.GetConnectionString("MessageBroker")
                 ?? "amqp://guest:guest@localhost:5672");

        cfg.UseMessageRetry(retry => retry.Intervals(200, 1000, 5000));
        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<NotificationsDbContext>("notifications-db", tags: ["ready"]);

var app = builder.Build();

await app.MigrateInDevelopmentAsync<NotificationsDbContext>();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapServiceDefaults();

// Scoped to the token's subject: a caller can only ever read their own feed.
// The legacy route took the user id as a query parameter on an unauthenticated
// endpoint, so anyone could read anyone's notifications.
app.MapGet("/api/users/me/notifications", async (
        NotificationsDbContext db,
        ICurrentUser currentUser,
        CancellationToken ct) =>
    {
        var userId = currentUser.RequireId();

        var notifications = await db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.RaisedOn)
            .Take(50)
            .Select(n => new { n.Id, n.Message, n.RaisedOn, n.IsRead })
            .ToListAsync(ct);

        return Results.Ok(notifications);
    })
    .RequireAuthorization()
    .WithTags("Notifications");

app.MapPost("/api/users/me/notifications/{id:int}/read", async (
        int id,
        NotificationsDbContext db,
        ICurrentUser currentUser,
        CancellationToken ct) =>
    {
        var userId = currentUser.RequireId();

        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);

        if (notification is null)
        {
            return Results.NotFound();
        }

        notification.MarkAsRead();
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    })
    .RequireAuthorization()
    .WithTags("Notifications");

app.MapHub<NotificationHub>("/hubs/notifications");

await app.RunAsync();

/// <summary>Exposed so the integration test host can reference the entry point assembly.</summary>
public partial class Program;
