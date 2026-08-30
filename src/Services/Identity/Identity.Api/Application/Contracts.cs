using Identity.Api.Domain;

namespace Identity.Api.Application;

// ---------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------

/// <summary>Credentials for sign-in. Exactly one of Id or Email must be supplied.</summary>
public sealed record LoginRequest(string? Id, string? Email, string Password);

public sealed record RegisterRequest(
    string Id,
    string FirstName,
    string LastName,
    string Email,
    string Password,
    string? Cin,
    string? PhoneNumber,
    string? Address);

public sealed record EmploymentDto(
    DateOnly? HiringDate,
    string? Status,
    string? ContractType,
    string? Position,
    string? LocalJobTitle,
    string? SiteCode,
    string? Site,
    string? Department,
    string? BusinessUnit,
    string? Segment);

public sealed record CreateUserRequest(
    string Id,
    string FirstName,
    string LastName,
    string? Cin,
    string? Email,
    string? Password,
    string? Address,
    string? PhoneNumber,
    string? Gender,
    string? SupervisorId,
    UserRole Role,
    EmploymentDto? Employment);

public sealed record UpdateUserRequest(
    string FirstName,
    string LastName,
    string? Cin,
    string? Email,
    string? Address,
    string? PhoneNumber,
    string? Gender,
    string? SupervisorId,
    UserRole Role,
    EmploymentDto? Employment);

public sealed record OpenAccountRequest(string Email, string Password, UserRole Role);

public sealed record ChangePasswordRequest(string NewPassword);

// ---------------------------------------------------------------------------
// Responses
// ---------------------------------------------------------------------------

/// <summary>
/// A user as returned by the API.
/// </summary>
/// <remarks>
/// Deliberately has no password field. The legacy endpoints serialised the entity
/// directly, so every user listing shipped the password hash of every user to the
/// browser. Projecting through an explicit contract makes that impossible.
/// </remarks>
public sealed record UserResponse(
    string Id,
    string FirstName,
    string LastName,
    string FullName,
    string? Cin,
    string? Email,
    string? Address,
    string? PhoneNumber,
    string? Gender,
    string? PhotoPath,
    EmploymentDto Employment,
    AccountStatus AccountStatus,
    UserRole Role,
    string? SupervisorId,
    string? SupervisorName,
    bool HasAccount,
    DateTimeOffset CreatedOn);

/// <summary>Trimmed projection for pickers and directory listings.</summary>
public sealed record UserSummaryResponse(
    string Id,
    string FullName,
    string? Email,
    string? Position,
    string? Department,
    string? PhotoPath);

public sealed record AuthenticationResponse(
    string Token,
    DateTimeOffset ExpiresOn,
    string UserId,
    string DisplayName,
    string Role,
    string? PhotoPath);

/// <summary>
/// No token is issued: the account is created Pending and an administrator must
/// activate it before first sign-in.
/// </summary>
public sealed record RegistrationResponse(string UserId, string DisplayName, bool AwaitingActivation);
