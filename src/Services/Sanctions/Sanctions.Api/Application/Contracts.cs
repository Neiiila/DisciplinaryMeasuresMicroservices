using Sanctions.Api.Domain;

namespace Sanctions.Api.Application;

public sealed record FaultResponse(int Id, string Title, string Category, bool IsValidated);

public sealed record ProposedFaultRequest(string Title, string Category);

/// <summary>
/// Raises a request. Supply exactly one of <paramref name="FaultId"/> or
/// <paramref name="ProposedFault"/>.
/// </summary>
public sealed record CreateSanctionRequestRequest(
    string Description,
    string Details,
    string EmployeeId,
    int? FaultId,
    ProposedFaultRequest? ProposedFault);

public sealed record RecordDecisionRequest(ValidationDecision Decision, string? Note);

public sealed record ValidationResponse(
    string ValidatorId,
    string? ValidatorName,
    ValidationDecision Decision,
    string? Note,
    DateTimeOffset DecidedOn);

public sealed record ProgressDto(int Completed, int Required, string Display);

public sealed record SanctionRequestSummaryResponse(
    int Id,
    string Description,
    DateTimeOffset RequestedOn,
    string EmployeeId,
    string? EmployeeName,
    string RequesterId,
    string? RequesterName,
    string? FaultTitle,
    ProgressDto Progress,
    string? CurrentValidatorId,
    bool IsCancelled,
    bool IsRefused,
    bool IsClosed);

public sealed record SanctionRequestResponse(
    int Id,
    string Description,
    string Details,
    DateTimeOffset RequestedOn,
    string EmployeeId,
    string? EmployeeName,
    string RequesterId,
    string? RequesterName,
    FaultResponse? Fault,
    string? AttachmentPath,
    ProgressDto Progress,
    string? CurrentValidatorId,
    string? CurrentValidatorName,
    bool IsCancelled,
    bool IsRefused,
    bool IsClosed,
    IReadOnlyList<ValidationResponse> Validations);
