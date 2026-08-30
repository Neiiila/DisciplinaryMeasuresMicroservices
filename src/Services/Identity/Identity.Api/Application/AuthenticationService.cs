using BuildingBlocks.Core.Abstractions;
using BuildingBlocks.Core.Results;
using BuildingBlocks.Messaging.Contracts;
using Identity.Api.Domain;
using Identity.Api.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Identity.Api.Application;

public interface IAuthenticationService
{
    Task<Result<AuthenticationResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default);

    Task<Result<RegistrationResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
}

public sealed class AuthenticationService(
    IdentityDbContext db,
    IPasswordHasher passwordHasher,
    IAccessTokenGenerator tokenGenerator,
    IPublishEndpoint publishEndpoint,
    IClock clock) : IAuthenticationService
{
    public async Task<Result<AuthenticationResponse>> LoginAsync(
        LoginRequest request,
        CancellationToken ct = default)
    {
        var id = Trimmed(request.Id);
        var email = Trimmed(request.Email);

        if (id is null && email is null)
        {
            return DomainErrors.MissingIdentifier;
        }

        var user = id is not null
            ? await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
            : await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        // Verify against a dummy hash when no user matched, so that a missing
        // account and a wrong password take the same time to answer. Skipping the
        // work would make user enumeration possible by timing alone.
        if (user is null)
        {
            passwordHasher.Verify(DummyHash, request.Password);
            return DomainErrors.InvalidCredentials;
        }

        var canSignIn = user.EnsureCanSignIn();
        if (canSignIn.IsFailure)
        {
            // Still verify, for the same reason.
            passwordHasher.Verify(user.PasswordHash ?? DummyHash, request.Password);
            return Result.Failure<AuthenticationResponse>(canSignIn.Error);
        }

        if (!passwordHasher.Verify(user.PasswordHash!, request.Password))
        {
            return DomainErrors.InvalidCredentials;
        }

        var (token, expiresOn) = tokenGenerator.Generate(user);

        return new AuthenticationResponse(
            token,
            expiresOn,
            user.Id,
            user.Name.Full,
            user.Role.ToString(),
            user.PhotoPath);
    }

    public async Task<Result<RegistrationResponse>> RegisterAsync(
        RegisterRequest request,
        CancellationToken ct = default)
    {
        var id = Trimmed(request.Id);
        var email = Trimmed(request.Email);

        if (id is null || email is null)
        {
            return DomainErrors.MissingIdentifier;
        }

        if (request.Password.Length < 8)
        {
            return DomainErrors.WeakPassword;
        }

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == id, ct))
        {
            return DomainErrors.IdTaken;
        }

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
        {
            return DomainErrors.EmailTaken;
        }

        // Self-registration always produces a Guest. Roles are granted by an
        // administrator; taking one from the request body would let a caller
        // choose their own privileges.
        var user = User.CreateEmployee(
            id,
            PersonName.Create(request.FirstName, request.LastName),
            UserRole.Guest,
            clock.UtcNow);

        user.UpdateProfile(
            PersonName.Create(request.FirstName, request.LastName),
            request.Cin,
            email,
            request.Address,
            request.PhoneNumber,
            gender: null,
            UserRole.Guest,
            EmploymentDetails.Empty);

        var opened = user.OpenAccount(email, passwordHasher.Hash(request.Password), UserRole.Guest);
        if (opened.IsFailure)
        {
            return Result.Failure<RegistrationResponse>(opened.Error);
        }

        db.Users.Add(user);

        // Published through the transactional outbox: the message is written in
        // this same transaction and dispatched afterwards, so a broker outage
        // cannot leave a user created with no event, nor an event with no user.
        await publishEndpoint.Publish(
            new AccountAwaitingActivation { EmployeeId = user.Id, FullName = user.Name.Full },
            ct);

        await publishEndpoint.Publish(ToProfileChanged(user), ct);

        await db.SaveChangesAsync(ct);

        return new RegistrationResponse(user.Id, user.Name.Full, AwaitingActivation: true);
    }

    internal static EmployeeProfileChanged ToProfileChanged(User user) => new()
    {
        EmployeeId = user.Id,
        FullName = user.Name.Full,
        Email = user.Email,
        SupervisorId = user.SupervisorId,
        Department = user.Employment.Department,
        Position = user.Employment.Position,
        Role = user.Role.ToString(),
        IsActive = !user.IsDeleted
    };

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A well-formed hash of a value nobody knows, for timing parity.</summary>
    private const string DummyHash =
        "210000.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
}
