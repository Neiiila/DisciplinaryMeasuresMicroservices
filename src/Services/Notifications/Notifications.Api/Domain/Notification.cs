namespace Notifications.Api.Domain;

/// <summary>An in-app message addressed to a single user.</summary>
public sealed class Notification
{
    private Notification()
    {
        Message = string.Empty;
        UserId = string.Empty;
    }

    private Notification(string userId, string message, DateTimeOffset raisedOn)
    {
        UserId = userId;
        Message = message;
        RaisedOn = raisedOn;
    }

    public int Id { get; private set; }

    public string UserId { get; private set; }

    public string Message { get; private set; }

    public DateTimeOffset RaisedOn { get; private set; }

    public bool IsRead { get; private set; }

    /// <summary>
    /// The event that produced this notification.
    /// </summary>
    /// <remarks>
    /// Stored so a redelivered message — the broker guarantees at-least-once —
    /// does not raise the same notification twice.
    /// </remarks>
    public Guid SourceEventId { get; private set; }

    public static Notification For(string userId, string message, Guid sourceEventId, DateTimeOffset raisedOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return new Notification(userId.Trim(), message.Trim(), raisedOn) { SourceEventId = sourceEventId };
    }

    public void MarkAsRead() => IsRead = true;
}
