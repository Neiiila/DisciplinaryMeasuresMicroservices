using BuildingBlocks.Web;
using BuildingBlocks.Web.Authentication;
using Identity.Api.Application;
using Microsoft.AspNetCore.Mvc;

namespace Identity.Api.Endpoints;

/// <summary>
/// Identity's HTTP surface.
/// </summary>
/// <remarks>
/// Routes keep the paths the gateway forwards unchanged, so a service can be
/// split or moved without the client noticing.
/// </remarks>
public static class IdentityEndpoints
{
    public static WebApplication MapIdentityEndpoints(this WebApplication app)
    {
        MapAuthentication(app);
        MapUsers(app);
        MapEmployees(app);

        return app;
    }

    private static void MapAuthentication(WebApplication app)
    {
        var group = app.MapGroup("/api/authentication")
            .AllowAnonymous()
            .WithTags("Authentication");

        group.MapPost("/login", async (
            LoginRequest request,
            IAuthenticationService authentication,
            CancellationToken ct) =>
            (await authentication.LoginAsync(request, ct)).ToHttpResult())
            .WithSummary("Exchanges credentials for an access token.");

        group.MapPost("/register", async (
            RegisterRequest request,
            IAuthenticationService authentication,
            CancellationToken ct) =>
            (await authentication.RegisterAsync(request, ct)).ToHttpResult())
            .WithSummary("Registers an account, pending an administrator's activation.");
    }

    private static void MapUsers(WebApplication app)
    {
        var group = app.MapGroup("/api/users")
            .RequireAuthorization()
            .WithTags("Users");

        group.MapGet("/", async (IUserService users, CancellationToken ct) =>
            Results.Ok(await users.GetAllAsync(ct)))
            .RequireAuthorization(Policies.Administrator);

        group.MapGet("/{id}", async (string id, IUserService users, CancellationToken ct) =>
            (await users.GetByIdAsync(id, ct)).ToHttpResult());

        group.MapPost("/", async (
            CreateUserRequest request,
            IUserService users,
            CancellationToken ct) =>
            (await users.CreateAsync(request, ct)).ToCreatedResult($"/api/users/{request.Id}"))
            .RequireAuthorization(Policies.Administrator);

        group.MapPut("/{id}", async (
            string id,
            UpdateUserRequest request,
            IUserService users,
            CancellationToken ct) =>
            (await users.UpdateAsync(id, request, ct)).ToHttpResult())
            .RequireAuthorization(Policies.Administrator);

        group.MapPost("/{id}/account", async (
            string id,
            OpenAccountRequest request,
            IUserService users,
            CancellationToken ct) =>
            (await users.OpenAccountAsync(id, request, ct)).ToHttpResult())
            .RequireAuthorization(Policies.Administrator);

        group.MapPost("/{id}/activation", async (string id, IUserService users, CancellationToken ct) =>
            (await users.ActivateAsync(id, ct)).ToHttpResult())
            .RequireAuthorization(Policies.Administrator);

        group.MapDelete("/{id}/account", async (string id, IUserService users, CancellationToken ct) =>
            (await users.RevokeAccountAsync(id, ct)).ToHttpResult())
            .RequireAuthorization(Policies.Administrator);

        group.MapDelete("/{id}", async (string id, IUserService users, CancellationToken ct) =>
            (await users.SoftDeleteAsync(id, ct)).ToHttpResult())
            .RequireAuthorization(Policies.Administrator);

        // Administrators may change anyone's password; everyone else only their own.
        group.MapPut("/{id}/password", async (
            string id,
            ChangePasswordRequest request,
            IUserService users,
            ICurrentUser currentUser,
            CancellationToken ct) =>
        {
            if (!currentUser.IsAdministrator
                && !string.Equals(id, currentUser.Id, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Forbid();
            }

            return (await users.ChangePasswordAsync(id, request, ct)).ToHttpResult();
        });
    }

    private static void MapEmployees(WebApplication app)
    {
        // The directory is a trimmed projection any authenticated user may read;
        // the full user record above is administrator-only.
        app.MapGet("/api/employees", async (IUserService users, CancellationToken ct) =>
            Results.Ok(await users.GetDirectoryAsync(ct)))
            .RequireAuthorization()
            .WithTags("Employees")
            .WithSummary("Read-only employee directory, for pickers and lists.");
    }
}
