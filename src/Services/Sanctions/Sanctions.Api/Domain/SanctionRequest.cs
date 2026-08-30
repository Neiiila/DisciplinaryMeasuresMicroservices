using BuildingBlocks.Core.Results;

namespace Sanctions.Api.Domain;

/// <summary>A supervisor's answer on a request.</summary>
public enum ValidationDecision
{
    /// <summary>The step elapsed without an answer.</summary>
    Missed = 0,
    Approved = 1,
    Refused = 2
}

/// <summary>
/// A disciplinary measure raised against an employee, travelling up the
/// employee's supervisor chain until every level has answered.
/// </summary>
/// <remarks>
/// <para>
/// The whole workflow lives on this aggregate. In the legacy code it was spread
/// across four service methods that each re-read and re-wrote the row, and two
/// defects came out of that split: the next validator was read from a navigation
/// property that was never loaded, so the chain silently stalled; and a duplicate
/// decision advanced the progress counter before the duplicate check ran.
/// </para>
/// <para>
/// Keeping the transition in one method means the guard and the state change
/// cannot be reordered or skipped by a new call site.
/// </para>
/// </remarks>
public sealed class SanctionRequest
{
    private readonly List<RequestValidation> _validations = [];

    private SanctionRequest()
    {
        Description = string.Empty;
        Details = string.Empty;
        EmployeeId = string.Empty;
        RequesterId = string.Empty;
    }

    private SanctionRequest(
        string employeeId,
        string requesterId,
        string description,
        string details,
        int? faultId,
        DateTimeOffset requestedOn)
    {
        EmployeeId = employeeId;
        RequesterId = requesterId;
        Description = description;
        Details = details;
        FaultId = faultId;
        RequestedOn = requestedOn;
    }

    public int Id { get; private set; }

    public string Description { get; private set; }

    public string Details { get; private set; }

    public DateTimeOffset RequestedOn { get; private set; }

    public string EmployeeId { get; private set; }

    public string RequesterId { get; private set; }

    public int? FaultId { get; private set; }

    public Fault? Fault { get; private set; }

    public string? AttachmentPath { get; private set; }

    /// <summary>Who the request is waiting on. Null once it is settled.</summary>
    public string? CurrentValidatorId { get; private set; }

    public int ApprovalsCollected { get; private set; }

    public int ApprovalsRequired { get; private set; }

    public bool IsCancelled { get; private set; }

    public bool IsRefused { get; private set; }

    /// <summary>True once the request can no longer change: approved, refused or cancelled.</summary>
    public bool IsClosed { get; private set; }

    public IReadOnlyList<RequestValidation> Validations => _validations;

    /// <summary>The "2/3" form the client renders.</summary>
    public string ProgressDisplay => $"{ApprovalsCollected}/{ApprovalsRequired}";

    /// <summary>
    /// Raises a request and routes it to the first validator in the chain.
    /// </summary>
    /// <param name="chain">
    /// The employee's supervisors, nearest first. Resolved by the caller from the
    /// local directory projection; an empty chain means nobody can validate.
    /// </param>
    public static Result<SanctionRequest> Raise(
        string employeeId,
        string requesterId,
        string description,
        string details,
        int? faultId,
        IReadOnlyList<string> chain,
        DateTimeOffset requestedOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(employeeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requesterId);

        if (string.IsNullOrWhiteSpace(description))
        {
            return DomainErrors.DescriptionRequired;
        }

        // Raising a request against yourself would put you in your own approval
        // chain, so the subject would decide their own sanction.
        if (string.Equals(employeeId, requesterId, StringComparison.OrdinalIgnoreCase))
        {
            return DomainErrors.SelfRequest;
        }

        if (chain.Count == 0)
        {
            return DomainErrors.NoValidationChain;
        }

        var request = new SanctionRequest(
            employeeId.Trim(),
            requesterId.Trim(),
            description.Trim(),
            details?.Trim() ?? string.Empty,
            faultId,
            requestedOn)
        {
            ApprovalsRequired = chain.Count,
            CurrentValidatorId = chain[0]
        };

        return request;
    }

    public void AttachFile(string? relativePath) =>
        AttachmentPath = string.IsNullOrWhiteSpace(relativePath) ? null : relativePath;

    public void AssignProposedFault(Fault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        Fault = fault;
    }

    /// <summary>
    /// Records one validator's answer and advances, refuses or completes the chain.
    /// </summary>
    /// <param name="chain">
    /// The employee's supervisors, nearest first, re-resolved at decision time so a
    /// reorganisation between raising and deciding is honoured.
    /// </param>
    public Result RecordDecision(
        string validatorId,
        ValidationDecision decision,
        string? note,
        IReadOnlyList<string> chain,
        DateTimeOffset decidedOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validatorId);

        if (IsClosed)
        {
            return Result.Failure(DomainErrors.RequestClosed);
        }

        if (!string.Equals(CurrentValidatorId, validatorId, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(DomainErrors.NotAwaitingCaller);
        }

        // Checked before any state changes. The legacy order advanced the counter
        // first, so a duplicate decision inflated progress even when rejected.
        if (_validations.Any(v => string.Equals(v.ValidatorId, validatorId, StringComparison.OrdinalIgnoreCase)))
        {
            return Result.Failure(DomainErrors.AlreadyAnswered);
        }

        _validations.Add(RequestValidation.Record(Id, validatorId, decision, note, decidedOn));

        if (decision == ValidationDecision.Refused)
        {
            IsRefused = true;
            Close();
            return Result.Success();
        }

        if (decision == ValidationDecision.Approved)
        {
            ApprovalsCollected++;
        }

        var next = NextValidatorAfter(validatorId, chain);

        if (next is null)
        {
            // Chain exhausted: the request is settled by whatever it collected.
            Close();
            return Result.Success();
        }

        CurrentValidatorId = next;
        return Result.Success();
    }

    /// <summary>Withdraws the request. Only its requester may do so, while it is open.</summary>
    public Result Cancel(string callerId)
    {
        if (!string.Equals(RequesterId, callerId, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(DomainErrors.NotRequester);
        }

        if (IsClosed)
        {
            return Result.Failure(DomainErrors.RequestClosed);
        }

        IsCancelled = true;
        Close();

        return Result.Success();
    }

    /// <summary>
    /// The next validator above <paramref name="validatorId"/> in the chain.
    /// </summary>
    /// <remarks>
    /// Resolved by position in the freshly supplied chain rather than from a stored
    /// pointer, so a validator who has left the chain between decisions does not
    /// strand the request.
    /// </remarks>
    private static string? NextValidatorAfter(string validatorId, IReadOnlyList<string> chain)
    {
        var index = -1;
        for (var i = 0; i < chain.Count; i++)
        {
            if (string.Equals(chain[i], validatorId, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0 || index + 1 >= chain.Count)
        {
            return null;
        }

        return chain[index + 1];
    }

    private void Close()
    {
        IsClosed = true;
        CurrentValidatorId = null;
    }
}
