namespace BuildingBlocks.Messaging.Contracts;

/// <summary>Published when a request is raised and routed to its first validator.</summary>
public sealed record SanctionRequestRaised : IntegrationEvent
{
    public required int RequestId { get; init; }

    public required string EmployeeId { get; init; }

    public required string RequesterId { get; init; }

    public required string Description { get; init; }

    /// <summary>Who the request is now waiting on. Null when nobody can validate it.</summary>
    public string? CurrentValidatorId { get; init; }
}

/// <summary>
/// Published when a request moves to the next validator in the chain.
/// Notifications uses it to tell that person they have something to answer.
/// </summary>
public sealed record SanctionRequestAwaitingValidator : IntegrationEvent
{
    public required int RequestId { get; init; }

    public required string ValidatorId { get; init; }

    public required string EmployeeName { get; init; }
}

/// <summary>Published once a request reaches a terminal state.</summary>
public sealed record SanctionRequestSettled : IntegrationEvent
{
    public required int RequestId { get; init; }

    public required string RequesterId { get; init; }

    public required string EmployeeId { get; init; }

    /// <summary>One of Approved, Refused or Cancelled.</summary>
    public required string Outcome { get; init; }
}
