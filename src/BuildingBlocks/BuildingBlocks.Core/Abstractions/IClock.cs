namespace BuildingBlocks.Core.Abstractions;

/// <summary>
/// The current time, injected so anything time-dependent stays testable.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Wall-clock implementation, always in UTC.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
