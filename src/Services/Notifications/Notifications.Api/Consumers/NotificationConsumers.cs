using BuildingBlocks.Messaging.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Notifications.Api.Domain;
using Notifications.Api.Infrastructure;
using Notifications.Api.Realtime;

namespace Notifications.Api.Consumers;

/// <summary>
/// Turns platform events into notifications, and pushes them live.
/// </summary>
/// <remarks>
/// This service is a pure consumer: nothing calls it to create a notification.
/// That inversion is the point of extracting it — Sanctions announces what
/// happened and does not need to know that anyone is listening, so adding a
/// second channel later (email, push) is a new consumer here rather than a change
/// to the workflow.
/// </remarks>
public sealed class SanctionAwaitingValidatorConsumer(
    NotificationsDbContext db,
    INotificationPublisher publisher) : IConsumer<SanctionRequestAwaitingValidator>
{
    public async Task Consume(ConsumeContext<SanctionRequestAwaitingValidator> context)
    {
        var message = context.Message;

        var notification = await RaiseAsync(
            db,
            message.ValidatorId,
            $"A sanction request concerning {message.EmployeeName} is awaiting your decision.",
            message.EventId,
            message.OccurredOn,
            context.CancellationToken);

        if (notification is not null)
        {
            await publisher.PushAsync(notification, context.CancellationToken);
        }
    }

    /// <summary>
    /// Writes a notification unless this event has already produced one.
    /// </summary>
    /// <remarks>
    /// The check is a query plus a unique index, not a query alone: two concurrent
    /// deliveries would both pass the query, and the index is what actually
    /// prevents the duplicate.
    /// </remarks>
    internal static async Task<Notification?> RaiseAsync(
        NotificationsDbContext db,
        string userId,
        string message,
        Guid sourceEventId,
        DateTimeOffset raisedOn,
        CancellationToken ct)
    {
        var alreadyRaised = await db.Notifications
            .AnyAsync(n => n.SourceEventId == sourceEventId && n.UserId == userId, ct);

        if (alreadyRaised)
        {
            return null;
        }

        var notification = Notification.For(userId, message, sourceEventId, raisedOn);
        db.Notifications.Add(notification);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race against a concurrent delivery of the same event. The
            // other one wrote the notification, so this delivery is complete.
            db.ChangeTracker.Clear();
            return null;
        }

        return notification;
    }
}

public sealed class SanctionSettledConsumer(
    NotificationsDbContext db,
    INotificationPublisher publisher) : IConsumer<SanctionRequestSettled>
{
    public async Task Consume(ConsumeContext<SanctionRequestSettled> context)
    {
        var message = context.Message;

        var text = message.Outcome switch
        {
            "Approved" => $"Your sanction request #{message.RequestId} has been approved.",
            "Refused" => $"Your sanction request #{message.RequestId} has been refused.",
            _ => $"Your sanction request #{message.RequestId} was cancelled."
        };

        var notification = await SanctionAwaitingValidatorConsumer.RaiseAsync(
            db, message.RequesterId, text, message.EventId, message.OccurredOn, context.CancellationToken);

        if (notification is not null)
        {
            await publisher.PushAsync(notification, context.CancellationToken);
        }
    }
}

/// <summary>
/// Tells administrators that an account is waiting to be activated.
/// </summary>
/// <remarks>
/// Broadcast rather than addressed: this service holds no directory, so it does
/// not know who the administrators are. Pushing to a SignalR group that only
/// administrators may join keeps that knowledge with the authorisation layer.
/// </remarks>
public sealed class AccountAwaitingActivationConsumer(INotificationPublisher publisher)
    : IConsumer<AccountAwaitingActivation>
{
    public Task Consume(ConsumeContext<AccountAwaitingActivation> context) =>
        publisher.PushToAdministratorsAsync(
            $"{context.Message.FullName} has registered and is awaiting activation.",
            context.CancellationToken);
}
