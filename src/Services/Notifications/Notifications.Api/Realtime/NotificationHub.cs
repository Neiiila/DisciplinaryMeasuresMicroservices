using System.Security.Claims;
using BuildingBlocks.Web.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Notifications.Api.Domain;

namespace Notifications.Api.Realtime;

/// <summary>
/// The live notification channel.
/// </summary>
/// <remarks>
/// Authorised, and scoped by the token's subject. The legacy hub took the user id
/// from a query-string parameter it never validated, so a client could subscribe
/// to anyone's notifications by supplying their matriculation number.
/// </remarks>
[Authorize]
public sealed class NotificationHub : Hub
{
    public const string AdministratorsGroup = "administrators";

    public override async Task OnConnectedAsync()
    {
        if (Context.User?.IsInRole(Roles.Administrator) == true)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AdministratorsGroup);
        }

        await base.OnConnectedAsync();
    }
}

public interface INotificationPublisher
{
    Task PushAsync(Notification notification, CancellationToken ct = default);

    Task PushToAdministratorsAsync(string message, CancellationToken ct = default);
}

public sealed class SignalRNotificationPublisher(IHubContext<NotificationHub> hub) : INotificationPublisher
{
    /// <summary>
    /// Delivers to the recipient's own connections.
    /// </summary>
    /// <remarks>
    /// SignalR resolves a user by the NameIdentifier claim, which is the same
    /// matriculation number the notification is addressed to, so no connection
    /// registry of our own is needed.
    /// </remarks>
    public Task PushAsync(Notification notification, CancellationToken ct = default) =>
        hub.Clients.User(notification.UserId).SendAsync(
            "notificationRaised",
            new
            {
                notification.Id,
                notification.Message,
                notification.RaisedOn,
                notification.IsRead
            },
            ct);

    public Task PushToAdministratorsAsync(string message, CancellationToken ct = default) =>
        hub.Clients.Group(NotificationHub.AdministratorsGroup).SendAsync(
            "administratorAlert",
            new { Message = message, RaisedOn = DateTimeOffset.UtcNow },
            ct);
}

/// <summary>Maps SignalR's user identity onto the token's subject claim.</summary>
public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirstValue(ClaimTypes.NameIdentifier);
}
