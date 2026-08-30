using System.Security.Claims;
using BuildingBlocks.Web.Authentication;
using Microsoft.AspNetCore.Http;

namespace BuildingBlocks.Web;

/// <summary>Identifies the caller behind the current request.</summary>
public interface ICurrentUser
{
    /// <summary>Matriculation number from the token, or null when unauthenticated.</summary>
    string? Id { get; }

    bool IsAdministrator { get; }

    /// <summary>The caller's id, or a throw when the endpoint should have required one.</summary>
    string RequireId();
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string? Id => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);

    public bool IsAdministrator => accessor.HttpContext?.User.IsInRole(Roles.Administrator) ?? false;

    public string RequireId() => Id
        ?? throw new InvalidOperationException(
            "The request reached an authorised endpoint without a subject claim.");
}
