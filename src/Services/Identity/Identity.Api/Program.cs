using BuildingBlocks.Web;
using BuildingBlocks.Web.Authentication;
using Identity.Api.Application;
using Identity.Api.Endpoints;
using Identity.Api.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddServiceDefaults("identity");
builder.Services.AddPlatformJwtAuthentication(builder.Configuration);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

builder.Services.AddDbContext<IdentityDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("IdentityDb")));

builder.Services.AddScoped<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddScoped<IAccessTokenGenerator, JwtAccessTokenGenerator>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
builder.Services.AddScoped<IUserService, UserService>();

builder.Services.AddMassTransit(bus =>
{
    // Transactional outbox. Published messages are written to Identity's own
    // database inside the same transaction as the entity change, then delivered
    // by a background dispatcher. Without it, a broker outage between SaveChanges
    // and Publish would leave a user created that no other service ever hears
    // about - the classic dual-write failure.
    bus.AddEntityFrameworkOutbox<IdentityDbContext>(outbox =>
    {
        outbox.UseSqlServer();
        outbox.UseBusOutbox();
    });

    bus.SetKebabCaseEndpointNameFormatter();

    bus.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration.GetConnectionString("MessageBroker")
                 ?? "amqp://guest:guest@localhost:5672");

        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<IdentityDbContext>("identity-db", tags: ["ready"]);

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
app.MapIdentityEndpoints();

await app.RunAsync();

/// <summary>Exposed so the integration test host can reference the entry point assembly.</summary>
public partial class Program;
