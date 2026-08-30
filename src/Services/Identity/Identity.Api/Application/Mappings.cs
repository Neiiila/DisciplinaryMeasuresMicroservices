using Identity.Api.Domain;

namespace Identity.Api.Application;

/// <summary>
/// Projects entities onto their API contracts.
/// </summary>
/// <remarks>
/// Ordinary methods rather than a convention-based mapper. The compiler checks
/// every field, and what is omitted — the password hash, most importantly — is
/// omitted visibly and on purpose.
/// </remarks>
public static class Mappings
{
    public static EmploymentDto ToDto(this EmploymentDetails employment) => new(
        employment.HiringDate,
        employment.Status,
        employment.ContractType,
        employment.Position,
        employment.LocalJobTitle,
        employment.SiteCode,
        employment.Site,
        employment.Department,
        employment.BusinessUnit,
        employment.Segment);

    public static EmploymentDetails ToDomain(this EmploymentDto? dto) => dto is null
        ? EmploymentDetails.Empty
        : new EmploymentDetails
        {
            HiringDate = dto.HiringDate,
            Status = dto.Status,
            ContractType = dto.ContractType,
            Position = dto.Position,
            LocalJobTitle = dto.LocalJobTitle,
            SiteCode = dto.SiteCode,
            Site = dto.Site,
            Department = dto.Department,
            BusinessUnit = dto.BusinessUnit,
            Segment = dto.Segment
        };

    public static UserResponse ToResponse(this User user) => new(
        user.Id,
        user.Name.First,
        user.Name.Last,
        user.Name.Full,
        user.Cin,
        user.Email,
        user.Address,
        user.PhoneNumber,
        user.Gender,
        user.PhotoPath,
        user.Employment.ToDto(),
        user.AccountStatus,
        user.Role,
        user.SupervisorId,
        user.Supervisor?.Name.Full,
        user.HasAccount,
        user.CreatedOn);

    public static UserSummaryResponse ToSummary(this User user) => new(
        user.Id,
        user.Name.Full,
        user.Email,
        user.Employment.Position,
        user.Employment.Department,
        user.PhotoPath);
}
