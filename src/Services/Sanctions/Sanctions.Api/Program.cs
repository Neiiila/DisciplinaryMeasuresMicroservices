using BuildingBlocks.Web;
using BuildingBlocks.Web.Authentication;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Sanctions.Api.Application;
using Sanctions.Api.Consumers;
using Sanctions.Api.Endpoints;
using Sanctions.Api.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddServiceDefaults("sanctions");
builder.Services.AddPlatformJwtAuthentication(builder.Configuration);

builder.Services.AddDbContext<SanctionsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("SanctionsDb")));

builder.Services.AddScoped<ISupervisorChainResolver, SupervisorChainResolver>();
builder.Services.AddScoped<ISanctionRequestService, SanctionRequestService>();

builder.Services.AddMassTransit(bus =>
{
    bus.AddEntityFrameworkOutbox<SanctionsDbContext>(outbox =>
    {
        outbox.UseSqlServer();
        outbox.UseBusOutbox();
    });

    // Consumers keep the local directory projection current from Identity's
    // events, which is what lets this service resolve a supervisor chain without
    // calling Identity on the request path.
    bus.AddConsumer<EmployeeProfileChangedConsumer>();
    bus.AddConsumer<EmployeeRemovedConsumer>();

    bus.SetKebabCaseEndpointNameFormatter();

    bus.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration.GetConnectionString("MessageBroker")
                 ?? "amqp://guest:guest@localhost:5672");

        // Retry transient faults in place, then move the message aside rather than
        // blocking the queue behind it.
        cfg.UseMessageRetry(retry => retry.Intervals(200, 1000, 5000));

        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<SanctionsDbContext>("sanctions-db", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapServiceDefaults();
app.MapSanctionEndpoints();

await app.RunAsync();

/// <summary>Exposed so the integration test host can reference the entry point assembly.</summary>
public partial class Program;
