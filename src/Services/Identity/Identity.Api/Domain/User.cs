using BuildingBlocks.Core.Results;

namespace Identity.Api.Domain;

/// <summary>
/// An employee record, optionally carrying a sign-in account.
/// </summary>
/// <remarks>
/// Employee and account are the same aggregate on purpose: a record can exist
/// without credentials (an employee who never signs in), but credentials cannot
/// exist without a record. Keeping them together makes "has an account" a property
/// of one entity rather than a join that every query has to remember.
/// </remarks>
public sealed class User
{
    private User()
    {
        Id = string.Empty;
        Name = PersonName.Create("placeholder", "placeholder");
        Employment = EmploymentDetails.Empty;
    }

    private User(string id, PersonName name, UserRole role, DateTimeOffset createdOn)
    {
        Id = id;
        Name = name;
        Role = role;
        CreatedOn = createdOn;
        Employment = EmploymentDetails.Empty;
        AccountStatus = AccountStatus.Pending;
    }

    /// <summary>Matriculation number. Assigned by HR, never generated here.</summary>
    public string Id { get; private set; }

    public PersonName Name { get; private set; }

    public string? Cin { get; private set; }

    public string? Email { get; private set; }

    public string? Address { get; private set; }

    public string? PhoneNumber { get; private set; }

    public string? Gender { get; private set; }

    public string? PhotoPath { get; private set; }

    public EmploymentDetails Employment { get; private set; }

    public UserRole Role { get; private set; }

    public AccountStatus AccountStatus { get; private set; }

    /// <summary>Null until an account is opened. Never leaves this service.</summary>
    public string? PasswordHash { get; private set; }

    public bool HasAccount => PasswordHash is not null;

    public string? SupervisorId { get; private set; }

    public User? Supervisor { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset CreatedOn { get; private set; }

    /// <summary>Creates an employee record with no sign-in account.</summary>
    public static User CreateEmployee(
        string id,
        PersonName name,
        UserRole role,
        DateTimeOffset createdOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(name);

        return new User(id.Trim(), name, role, createdOn);
    }

    /// <summary>
    /// Opens a sign-in account. The account starts <see cref="AccountStatus.Pending"/>:
    /// an administrator must activate it before the user can sign in.
    /// </summary>
    public Result OpenAccount(string email, string passwordHash, UserRole role)
    {
        if (HasAccount)
        {
            return Result.Failure(DomainErrors.AccountAlreadyOpen);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        Email = email.Trim();
        PasswordHash = passwordHash;
        Role = role;
        AccountStatus = AccountStatus.Pending;

        return Result.Success();
    }

    public Result Activate()
    {
        if (!HasAccount)
        {
            return Result.Failure(DomainErrors.NoAccountToActivate);
        }

        AccountStatus = AccountStatus.Active;
        return Result.Success();
    }

    /// <summary>Withdraws sign-in rights. The employee record is retained.</summary>
    public Result RevokeAccount()
    {
        if (!HasAccount)
        {
            return Result.Failure(DomainErrors.NoAccountToRevoke);
        }

        AccountStatus = AccountStatus.Revoked;
        return Result.Success();
    }

    /// <summary>
    /// Whether this user may currently sign in, and why not when they may not.
    /// </summary>
    /// <remarks>
    /// Expressed on the entity rather than in the sign-in service so that every
    /// caller applies the same rule. The legacy code checked status inline at the
    /// one call site that happened to need it.
    /// </remarks>
    public Result EnsureCanSignIn() => AccountStatus switch
    {
        _ when !HasAccount || IsDeleted => Result.Failure(DomainErrors.InvalidCredentials),
        AccountStatus.Pending => Result.Failure(DomainErrors.AccountAwaitingActivation),
        AccountStatus.Revoked => Result.Failure(DomainErrors.AccountRevoked),
        _ => Result.Success()
    };

    public void SetPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public void UpdateProfile(
        PersonName name,
        string? cin,
        string? email,
        string? address,
        string? phoneNumber,
        string? gender,
        UserRole role,
        EmploymentDetails employment)
    {
        ArgumentNullException.ThrowIfNull(name);

        Name = name;
        Cin = Normalise(cin);
        Email = Normalise(email);
        Address = Normalise(address);
        PhoneNumber = Normalise(phoneNumber);
        Gender = Normalise(gender);
        Role = role;
        Employment = employment ?? EmploymentDetails.Empty;
    }

    /// <summary>
    /// Sets who validates this user's requests.
    /// </summary>
    /// <remarks>
    /// Self-supervision is rejected here because it would produce a chain that never
    /// terminates — the request would route to its own subject forever.
    /// </remarks>
    public Result AssignSupervisor(string? supervisorId)
    {
        var normalised = Normalise(supervisorId);

        if (normalised is not null && string.Equals(normalised, Id, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(DomainErrors.SelfSupervision);
        }

        SupervisorId = normalised;
        return Result.Success();
    }

    public void SetPhoto(string? relativePath) => PhotoPath = Normalise(relativePath);

    /// <summary>Hides the record from listings while retaining the row for audit.</summary>
    public void SoftDelete()
    {
        IsDeleted = true;
        AccountStatus = AccountStatus.Revoked;
    }

    private static string? Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
