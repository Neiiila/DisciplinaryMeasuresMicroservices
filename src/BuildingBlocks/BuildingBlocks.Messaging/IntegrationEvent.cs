namespace BuildingBlocks.Messaging;

/// <summary>
/// Base for every message published across service boundaries.
/// </summary>
/// <remarks>
/// Integration events are a published contract: once another service consumes one,
/// its shape may only be extended, never changed. They carry primitives and ids
/// rather than domain objects, so no service takes a compile-time dependency on
/// another's internal model.
/// </remarks>
public abstract record IntegrationEvent
{
    /// <summary>Stable identity, used for idempotent consumption.</summary>
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    /// <summary>When the publishing service raised the event.</summary>
    public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
}
