namespace BuildingBlocks.Messaging.Contracts;

/// <summary>
/// Published by Identity whenever an employee record is created or changed.
/// </summary>
/// <remarks>
/// Sanctions keeps a local read model of the directory so it can resolve names and
/// walk the supervisor chain without calling Identity on every request. This event
/// is what keeps that copy current. It is deliberately a full snapshot rather than a
/// delta: a consumer that misses one event still converges on the next, which makes
/// the projection self-healing.
/// </remarks>
public sealed record EmployeeProfileChanged : IntegrationEvent
{
    public required string EmployeeId { get; init; }

    public required string FullName { get; init; }

    public string? Email { get; init; }

    /// <summary>Null for the top of a chain, which ends validation.</summary>
    public string? SupervisorId { get; init; }

    public string? Department { get; init; }

    public string? Position { get; init; }

    /// <summary>Role name, matching Identity's UserRole enum.</summary>
    public required string Role { get; init; }

    /// <summary>False once the record is soft-deleted, so consumers can hide it.</summary>
    public required bool IsActive { get; init; }
}

/// <summary>Published when an employee record is soft-deleted in Identity.</summary>
public sealed record EmployeeRemoved : IntegrationEvent
{
    public required string EmployeeId { get; init; }
}

/// <summary>
/// Published when an account is registered and awaits activation.
/// Notifications turns this into a message for administrators.
/// </summary>
public sealed record AccountAwaitingActivation : IntegrationEvent
{
    public required string EmployeeId { get; init; }

    public required string FullName { get; init; }
}
