using BuildingBlocks.Web;
using BuildingBlocks.Web.Authentication;
using Sanctions.Api.Application;

namespace Sanctions.Api.Endpoints;

public static class SanctionEndpoints
{
    public static WebApplication MapSanctionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sanction-requests")
            .RequireAuthorization()
            .WithTags("Sanction requests");

        group.MapGet("/", async (ISanctionRequestService service, CancellationToken ct) =>
            Results.Ok(await service.GetAllAsync(ct)))
            .RequireAuthorization(Policies.Administrator)
            .WithSummary("Every request in the system.");

        group.MapGet("/{id:int}", async (int id, ISanctionRequestService service, CancellationToken ct) =>
            (await service.GetByIdAsync(id, ct)).ToHttpResult());

        group.MapGet("/mine", async (
            ISanctionRequestService service,
            ICurrentUser currentUser,
            CancellationToken ct) =>
            Results.Ok(await service.GetRaisedByAsync(currentUser.RequireId(), ct)));

        group.MapGet("/addressed-to-me", async (
            ISanctionRequestService service,
            ICurrentUser currentUser,
            CancellationToken ct) =>
            Results.Ok(await service.GetAddressedToAsync(currentUser.RequireId(), ct)));

        // The requester comes from the token, never the body, so a caller cannot
        // raise a request in someone else's name.
        group.MapPost("/", async (
            CreateSanctionRequestRequest request,
            ISanctionRequestService service,
            ICurrentUser currentUser,
            CancellationToken ct) =>
        {
            var result = await service.RaiseAsync(currentUser.RequireId(), request, ct);

            return result.IsSuccess
                ? result.ToCreatedResult($"/api/sanction-requests/{result.Value.Id}")
                : result.ToHttpResult();
        });

        group.MapPost("/{id:int}/decisions", async (
            int id,
            RecordDecisionRequest decision,
            ISanctionRequestService service,
            ICurrentUser currentUser,
            CancellationToken ct) =>
            (await service.RecordDecisionAsync(id, currentUser.RequireId(), decision, ct)).ToHttpResult());

        group.MapPost("/{id:int}/cancellation", async (
            int id,
            ISanctionRequestService service,
            ICurrentUser currentUser,
            CancellationToken ct) =>
            (await service.CancelAsync(id, currentUser.RequireId(), ct)).ToHttpResult());

        app.MapGet("/api/faults", async (ISanctionRequestService service, CancellationToken ct) =>
            Results.Ok(await service.GetFaultCatalogueAsync(ct)))
            .RequireAuthorization()
            .WithTags("Faults")
            .WithSummary("The validated fault catalogue, for the request form's picker.");

        return app;
    }
}
