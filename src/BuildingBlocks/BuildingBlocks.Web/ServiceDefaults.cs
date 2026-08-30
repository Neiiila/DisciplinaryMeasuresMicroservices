using System.Text.Json.Serialization;
using BuildingBlocks.Core.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BuildingBlocks.Web;

/// <summary>
/// Cross-cutting setup every service applies identically.
/// </summary>
/// <remarks>
/// Each service is its own process, so anything not centralised here has to be
/// remembered five times. Enum serialisation is the clearest example: if one
/// service emitted ordinals while the others emitted names, the client would see
/// the same field as a number on some responses and a string on others.
/// </remarks>
public static class ServiceDefaults
{
    public static IServiceCollection AddServiceDefaults(
        this IServiceCollection services,
        string serviceName)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<ICurrentUser, CurrentUser>();

        services.AddProblemDetails();
        services.AddOpenApi();

        services.ConfigureHttpJsonOptions(options =>
        {
            // Enums travel as their names so the client never tracks ordinals.
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        // Liveness answers "is the process up"; readiness additionally requires the
        // dependencies tagged "ready", so an orchestrator does not route traffic to
        // an instance whose database is still starting.
        services.AddHealthChecks();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        return services;
    }

    public static WebApplication MapServiceDefaults(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        });

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        });

        return app;
    }
}
