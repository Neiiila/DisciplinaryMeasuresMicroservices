using BuildingBlocks.Core.Abstractions;
using BuildingBlocks.Core.Results;
using BuildingBlocks.Messaging.Contracts;
using Identity.Api.Domain;
using Identity.Api.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Identity.Api.Application;

public interface IUserService
{
    Task<IReadOnlyList<UserResponse>> GetAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<UserSummaryResponse>> GetDirectoryAsync(CancellationToken ct = default);

    Task<Result<UserResponse>> GetByIdAsync(string id, CancellationToken ct = default);

    Task<Result<UserResponse>> CreateAsync(CreateUserRequest request, CancellationToken ct = default);

    Task<Result<UserResponse>> UpdateAsync(string id, UpdateUserRequest request, CancellationToken ct = default);

    Task<Result<UserResponse>> OpenAccountAsync(string id, OpenAccountRequest request, CancellationToken ct = default);

    Task<Result> ActivateAsync(string id, CancellationToken ct = default);

    Task<Result> RevokeAccountAsync(string id, CancellationToken ct = default);

    Task<Result> SoftDeleteAsync(string id, CancellationToken ct = default);

    Task<Result> ChangePasswordAsync(string id, ChangePasswordRequest request, CancellationToken ct = default);
}

public sealed class UserService(
    IdentityDbContext db,
    IPasswordHasher passwordHasher,
    IPublishEndpoint publishEndpoint,
    IClock clock) : IUserService
{
    public async Task<IReadOnlyList<UserResponse>> GetAllAsync(CancellationToken ct = default) =>
        await db.Users
            .AsNoTracking()
            .Include(u => u.Supervisor)
            .OrderBy(u => u.Name.Last)
            .Select(u => u.ToResponse())
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserSummaryResponse>> GetDirectoryAsync(CancellationToken ct = default) =>
        await db.Users
            .AsNoTracking()
            .OrderBy(u => u.Name.Last)
            .Select(u => u.ToSummary())
            .ToListAsync(ct);

    public async Task<Result<UserResponse>> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var user = await db.Users
            .AsNoTracking()
            .Include(u => u.Supervisor)
            .FirstOrDefaultAsync(u => u.Id == id, ct);

        return user is null ? DomainErrors.UserNotFound : user.ToResponse();
    }

    public async Task<Result<UserResponse>> CreateAsync(
        CreateUserRequest request,
        CancellationToken ct = default)
    {
        var id = request.Id.Trim();

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == id, ct))
        {
            return DomainErrors.IdTaken;
        }

        var email = Trimmed(request.Email);
        if (email is not null && await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
        {
            return DomainErrors.EmailTaken;
        }

        var user = User.CreateEmployee(
            id,
            PersonName.Create(request.FirstName, request.LastName),
            request.Role,
            clock.UtcNow);

        user.UpdateProfile(
            PersonName.Create(request.FirstName, request.LastName),
            request.Cin,
            email,
            request.Address,
            request.PhoneNumber,
            request.Gender,
            request.Role,
            request.Employment.ToDomain());

        var supervisorAssigned = await AssignSupervisorAsync(user, request.SupervisorId, ct);
        if (supervisorAssigned.IsFailure)
        {
            return Result.Failure<UserResponse>(supervisorAssigned.Error);
        }

        // A password is optional: an employee record may exist with no way to sign
        // in, and an account can be opened later.
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            if (request.Password.Length < 8)
            {
                return DomainErrors.WeakPassword;
            }

            if (email is null)
            {
                return Error.Validation("user.email_required", "An email address is required to open an account.");
            }

            var opened = user.OpenAccount(email, passwordHasher.Hash(request.Password), request.Role);
            if (opened.IsFailure)
            {
                return Result.Failure<UserResponse>(opened.Error);
            }
        }

        db.Users.Add(user);
        await publishEndpoint.Publish(AuthenticationService.ToProfileChanged(user), ct);
        await db.SaveChangesAsync(ct);

        return user.ToResponse();
    }

    public async Task<Result<UserResponse>> UpdateAsync(
        string id,
        UpdateUserRequest request,
        CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return DomainErrors.UserNotFound;
        }

        var email = Trimmed(request.Email);
        if (email is not null
            && await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email && u.Id != id, ct))
        {
            return DomainErrors.EmailTaken;
        }

        // Every field is applied as given. The legacy update copied properties by
        // reflection and skipped nulls, which meant a field could never be cleared
        // once set.
        user.UpdateProfile(
            PersonName.Create(request.FirstName, request.LastName),
            request.Cin,
            email,
            request.Address,
            request.PhoneNumber,
            request.Gender,
            request.Role,
            request.Employment.ToDomain());

        var supervisorAssigned = await AssignSupervisorAsync(user, request.SupervisorId, ct);
        if (supervisorAssigned.IsFailure)
        {
            return Result.Failure<UserResponse>(supervisorAssigned.Error);
        }

        await publishEndpoint.Publish(AuthenticationService.ToProfileChanged(user), ct);
        await db.SaveChangesAsync(ct);

        return user.ToResponse();
    }

    public async Task<Result<UserResponse>> OpenAccountAsync(
        string id,
        OpenAccountRequest request,
        CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return DomainErrors.UserNotFound;
        }

        if (request.Password.Length < 8)
        {
            return DomainErrors.WeakPassword;
        }

        var email = request.Email.Trim();
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email && u.Id != id, ct))
        {
            return DomainErrors.EmailTaken;
        }

        var opened = user.OpenAccount(email, passwordHasher.Hash(request.Password), request.Role);
        if (opened.IsFailure)
        {
            return Result.Failure<UserResponse>(opened.Error);
        }

        await publishEndpoint.Publish(
            new AccountAwaitingActivation { EmployeeId = user.Id, FullName = user.Name.Full },
            ct);

        await db.SaveChangesAsync(ct);

        return user.ToResponse();
    }

    public Task<Result> ActivateAsync(string id, CancellationToken ct = default) =>
        MutateAsync(id, user => user.Activate(), ct);

    public Task<Result> RevokeAccountAsync(string id, CancellationToken ct = default) =>
        MutateAsync(id, user => user.RevokeAccount(), ct);

    public async Task<Result> SoftDeleteAsync(string id, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Result.Failure(DomainErrors.UserNotFound);
        }

        user.SoftDelete();

        // Consumers hide the employee from pickers on this. Their own records
        // referencing the id stay intact, which is what keeps history readable.
        await publishEndpoint.Publish(new EmployeeRemoved { EmployeeId = user.Id }, ct);
        await db.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<Result> ChangePasswordAsync(
        string id,
        ChangePasswordRequest request,
        CancellationToken ct = default)
    {
        if (request.NewPassword.Length < 8)
        {
            return Result.Failure(DomainErrors.WeakPassword);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Result.Failure(DomainErrors.UserNotFound);
        }

        user.SetPasswordHash(passwordHasher.Hash(request.NewPassword));
        await db.SaveChangesAsync(ct);

        return Result.Success();
    }

    private async Task<Result> MutateAsync(string id, Func<User, Result> mutate, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Result.Failure(DomainErrors.UserNotFound);
        }

        var result = mutate(user);
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task<Result> AssignSupervisorAsync(User user, string? supervisorId, CancellationToken ct)
    {
        var normalised = Trimmed(supervisorId);

        if (normalised is not null && !await db.Users.AnyAsync(u => u.Id == normalised, ct))
        {
            return Result.Failure(DomainErrors.SupervisorNotFound);
        }

        return user.AssignSupervisor(normalised);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
