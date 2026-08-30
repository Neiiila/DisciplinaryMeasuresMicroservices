using BuildingBlocks.Core.Abstractions;
using BuildingBlocks.Core.Results;
using BuildingBlocks.Messaging.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Sanctions.Api.Domain;
using Sanctions.Api.Infrastructure;

// MassTransit also defines a Fault<T> type, so the domain's Fault is aliased
// rather than left to an ambiguous reference.
using Fault = Sanctions.Api.Domain.Fault;

namespace Sanctions.Api.Application;

public interface ISanctionRequestService
{
    Task<IReadOnlyList<SanctionRequestSummaryResponse>> GetAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SanctionRequestSummaryResponse>> GetRaisedByAsync(string userId, CancellationToken ct = default);

    Task<IReadOnlyList<SanctionRequestSummaryResponse>> GetAddressedToAsync(string userId, CancellationToken ct = default);

    Task<Result<SanctionRequestResponse>> GetByIdAsync(int id, CancellationToken ct = default);

    Task<Result<SanctionRequestResponse>> RaiseAsync(
        string requesterId, CreateSanctionRequestRequest request, CancellationToken ct = default);

    Task<Result<SanctionRequestResponse>> RecordDecisionAsync(
        int id, string validatorId, RecordDecisionRequest decision, CancellationToken ct = default);

    Task<Result> CancelAsync(int id, string callerId, CancellationToken ct = default);

    Task<IReadOnlyList<FaultResponse>> GetFaultCatalogueAsync(CancellationToken ct = default);
}

public sealed class SanctionRequestService(
    SanctionsDbContext db,
    ISupervisorChainResolver chainResolver,
    IPublishEndpoint publishEndpoint,
    IClock clock) : ISanctionRequestService
{
    public Task<IReadOnlyList<SanctionRequestSummaryResponse>> GetAllAsync(CancellationToken ct = default) =>
        SummariesAsync(db.SanctionRequests, ct);

    public Task<IReadOnlyList<SanctionRequestSummaryResponse>> GetRaisedByAsync(
        string userId, CancellationToken ct = default) =>
        SummariesAsync(db.SanctionRequests.Where(r => r.RequesterId == userId), ct);

    /// <summary>
    /// Requests awaiting this user, plus the ones they have already answered.
    /// </summary>
    /// <remarks>
    /// The second half is what makes the screen useful after a decision: without
    /// it, answering a request would make it vanish with no confirmation.
    /// </remarks>
    public Task<IReadOnlyList<SanctionRequestSummaryResponse>> GetAddressedToAsync(
        string userId, CancellationToken ct = default) =>
        SummariesAsync(
            db.SanctionRequests.Where(r =>
                r.CurrentValidatorId == userId
                || r.Validations.Any(v => v.ValidatorId == userId)),
            ct);

    public async Task<Result<SanctionRequestResponse>> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var request = await db.SanctionRequests
            .AsNoTracking()
            .Include(r => r.Fault)
            .Include(r => r.Validations)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (request is null)
        {
            return DomainErrors.RequestNotFound;
        }

        return await ToResponseAsync(request, ct);
    }

    public async Task<Result<SanctionRequestResponse>> RaiseAsync(
        string requesterId,
        CreateSanctionRequestRequest request,
        CancellationToken ct = default)
    {
        // Exactly one of the two must be supplied. Accepting both would leave it
        // ambiguous which fault the request actually cites.
        if (request.FaultId is null && request.ProposedFault is null)
        {
            return DomainErrors.FaultRequired;
        }

        if (request.FaultId is not null && request.ProposedFault is not null)
        {
            return DomainErrors.AmbiguousFault;
        }

        var employee = await db.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.IsActive, ct);

        if (employee is null)
        {
            return DomainErrors.EmployeeNotFound;
        }

        if (request.FaultId is not null
            && !await db.Faults.AnyAsync(f => f.Id == request.FaultId, ct))
        {
            return DomainErrors.FaultNotFound;
        }

        var chain = await chainResolver.ResolveAsync(request.EmployeeId, ct);

        var raised = SanctionRequest.Raise(
            request.EmployeeId,
            requesterId,
            request.Description,
            request.Details,
            request.FaultId,
            chain,
            clock.UtcNow);

        if (raised.IsFailure)
        {
            return Result.Failure<SanctionRequestResponse>(raised.Error);
        }

        var entity = raised.Value;

        if (request.ProposedFault is not null)
        {
            entity.AssignProposedFault(
                Fault.Propose(request.ProposedFault.Title, request.ProposedFault.Category));
        }

        db.SanctionRequests.Add(entity);
        await db.SaveChangesAsync(ct);

        await PublishRaisedAsync(entity, employee.FullName, ct);
        await db.SaveChangesAsync(ct);

        return await ToResponseAsync(entity, ct);
    }

    public async Task<Result<SanctionRequestResponse>> RecordDecisionAsync(
        int id,
        string validatorId,
        RecordDecisionRequest decision,
        CancellationToken ct = default)
    {
        var request = await db.SanctionRequests
            .Include(r => r.Fault)
            .Include(r => r.Validations)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (request is null)
        {
            return DomainErrors.RequestNotFound;
        }

        // Re-resolved rather than stored, so a reorganisation between raising and
        // deciding is honoured as the request travels up.
        var chain = await chainResolver.ResolveAsync(request.EmployeeId, ct);

        var recorded = request.RecordDecision(validatorId, decision.Decision, decision.Note, chain, clock.UtcNow);
        if (recorded.IsFailure)
        {
            return Result.Failure<SanctionRequestResponse>(recorded.Error);
        }

        await PublishAfterDecisionAsync(request, ct);
        await db.SaveChangesAsync(ct);

        return await ToResponseAsync(request, ct);
    }

    public async Task<Result> CancelAsync(int id, string callerId, CancellationToken ct = default)
    {
        var request = await db.SanctionRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return Result.Failure(DomainErrors.RequestNotFound);
        }

        var cancelled = request.Cancel(callerId);
        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        await publishEndpoint.Publish(
            new SanctionRequestSettled
            {
                RequestId = request.Id,
                RequesterId = request.RequesterId,
                EmployeeId = request.EmployeeId,
                Outcome = "Cancelled"
            },
            ct);

        await db.SaveChangesAsync(ct);

        return Result.Success();
    }

    public async Task<IReadOnlyList<FaultResponse>> GetFaultCatalogueAsync(CancellationToken ct = default) =>
        await db.Faults
            .AsNoTracking()
            .Where(f => f.IsValidated)
            .OrderBy(f => f.Category).ThenBy(f => f.Title)
            .Select(f => new FaultResponse(f.Id, f.Title, f.Category, f.IsValidated))
            .ToListAsync(ct);

    private async Task PublishRaisedAsync(SanctionRequest request, string employeeName, CancellationToken ct)
    {
        await publishEndpoint.Publish(
            new SanctionRequestRaised
            {
                RequestId = request.Id,
                EmployeeId = request.EmployeeId,
                RequesterId = request.RequesterId,
                Description = request.Description,
                CurrentValidatorId = request.CurrentValidatorId
            },
            ct);

        if (request.CurrentValidatorId is not null)
        {
            await publishEndpoint.Publish(
                new SanctionRequestAwaitingValidator
                {
                    RequestId = request.Id,
                    ValidatorId = request.CurrentValidatorId,
                    EmployeeName = employeeName
                },
                ct);
        }
    }

    private async Task PublishAfterDecisionAsync(SanctionRequest request, CancellationToken ct)
    {
        if (request.IsClosed)
        {
            await publishEndpoint.Publish(
                new SanctionRequestSettled
                {
                    RequestId = request.Id,
                    RequesterId = request.RequesterId,
                    EmployeeId = request.EmployeeId,
                    Outcome = request.IsRefused ? "Refused" : "Approved"
                },
                ct);

            return;
        }

        if (request.CurrentValidatorId is not null)
        {
            var employeeName = await db.Employees
                .Where(e => e.Id == request.EmployeeId)
                .Select(e => e.FullName)
                .FirstOrDefaultAsync(ct) ?? request.EmployeeId;

            await publishEndpoint.Publish(
                new SanctionRequestAwaitingValidator
                {
                    RequestId = request.Id,
                    ValidatorId = request.CurrentValidatorId,
                    EmployeeName = employeeName
                },
                ct);
        }
    }

    /// <summary>
    /// Projects requests to summaries, resolving display names from the local
    /// directory in one extra query rather than one per row.
    /// </summary>
    private async Task<IReadOnlyList<SanctionRequestSummaryResponse>> SummariesAsync(
        IQueryable<SanctionRequest> query,
        CancellationToken ct)
    {
        var rows = await query
            .AsNoTracking()
            .OrderByDescending(r => r.RequestedOn)
            .Select(r => new
            {
                r.Id,
                r.Description,
                r.RequestedOn,
                r.EmployeeId,
                r.RequesterId,
                FaultTitle = r.Fault != null ? r.Fault.Title : null,
                r.ApprovalsCollected,
                r.ApprovalsRequired,
                r.CurrentValidatorId,
                r.IsCancelled,
                r.IsRefused,
                r.IsClosed
            })
            .ToListAsync(ct);

        var names = await NamesForAsync(
            rows.SelectMany(r => new[] { r.EmployeeId, r.RequesterId }),
            ct);

        return rows
            .Select(r => new SanctionRequestSummaryResponse(
                r.Id,
                r.Description,
                r.RequestedOn,
                r.EmployeeId,
                names.GetValueOrDefault(r.EmployeeId),
                r.RequesterId,
                names.GetValueOrDefault(r.RequesterId),
                r.FaultTitle,
                new ProgressDto(r.ApprovalsCollected, r.ApprovalsRequired, $"{r.ApprovalsCollected}/{r.ApprovalsRequired}"),
                r.CurrentValidatorId,
                r.IsCancelled,
                r.IsRefused,
                r.IsClosed))
            .ToList();
    }

    private async Task<SanctionRequestResponse> ToResponseAsync(SanctionRequest request, CancellationToken ct)
    {
        var ids = new List<string> { request.EmployeeId, request.RequesterId };
        ids.AddRange(request.Validations.Select(v => v.ValidatorId));
        if (request.CurrentValidatorId is not null)
        {
            ids.Add(request.CurrentValidatorId);
        }

        var names = await NamesForAsync(ids, ct);

        return new SanctionRequestResponse(
            request.Id,
            request.Description,
            request.Details,
            request.RequestedOn,
            request.EmployeeId,
            names.GetValueOrDefault(request.EmployeeId),
            request.RequesterId,
            names.GetValueOrDefault(request.RequesterId),
            request.Fault is null
                ? null
                : new FaultResponse(request.Fault.Id, request.Fault.Title, request.Fault.Category, request.Fault.IsValidated),
            request.AttachmentPath,
            new ProgressDto(request.ApprovalsCollected, request.ApprovalsRequired, request.ProgressDisplay),
            request.CurrentValidatorId,
            request.CurrentValidatorId is null ? null : names.GetValueOrDefault(request.CurrentValidatorId),
            request.IsCancelled,
            request.IsRefused,
            request.IsClosed,
            [.. request.Validations
                .OrderBy(v => v.DecidedOn)
                .Select(v => new ValidationResponse(
                    v.ValidatorId,
                    names.GetValueOrDefault(v.ValidatorId),
                    v.Decision,
                    v.Note,
                    v.DecidedOn))]);
    }

    private async Task<Dictionary<string, string>> NamesForAsync(
        IEnumerable<string> ids,
        CancellationToken ct)
    {
        var distinct = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (distinct.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return await db.Employees
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(e => distinct.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.FullName, StringComparer.OrdinalIgnoreCase, ct);
    }
}
