using BuildingBlocks.Core.Results;

namespace Sanctions.Api.Domain;

/// <summary>A catalogued misconduct type that a request cites.</summary>
public sealed class Fault
{
    private Fault()
    {
        Title = string.Empty;
        Category = string.Empty;
    }

    private Fault(string title, string category, bool isValidated)
    {
        Title = title;
        Category = category;
        IsValidated = isValidated;
    }

    public int Id { get; private set; }

    public string Title { get; private set; }

    public string Category { get; private set; }

    /// <summary>Whether an administrator has accepted this fault into the catalogue.</summary>
    public bool IsValidated { get; private set; }

    /// <summary>Creates a fault proposed while raising a request; not yet catalogued.</summary>
    public static Fault Propose(string title, string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        return new Fault(title.Trim(), string.IsNullOrWhiteSpace(category) ? "Uncategorised" : category.Trim(), false);
    }

    public static Fault CreateValidated(string title, string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        return new Fault(title.Trim(), category.Trim(), true);
    }

    public void Validate() => IsValidated = true;
}

/// <summary>One recorded answer in a request's history.</summary>
public sealed class RequestValidation
{
    private RequestValidation()
    {
        ValidatorId = string.Empty;
    }

    private RequestValidation(
        int sanctionRequestId,
        string validatorId,
        ValidationDecision decision,
        string? note,
        DateTimeOffset decidedOn)
    {
        SanctionRequestId = sanctionRequestId;
        ValidatorId = validatorId;
        Decision = decision;
        Note = note;
        DecidedOn = decidedOn;
    }

    public int Id { get; private set; }

    public int SanctionRequestId { get; private set; }

    public string ValidatorId { get; private set; }

    public ValidationDecision Decision { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset DecidedOn { get; private set; }

    internal static RequestValidation Record(
        int sanctionRequestId,
        string validatorId,
        ValidationDecision decision,
        string? note,
        DateTimeOffset decidedOn) =>
        new(sanctionRequestId, validatorId, decision, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), decidedOn);
}

/// <summary>
/// Sanctions' local copy of the parts of the employee directory it needs.
/// </summary>
/// <remarks>
/// <para>
/// This is the answer to the central problem of splitting the monolith: raising a
/// request needs the employee's supervisor chain, which Identity owns. Calling
/// Identity synchronously on every request would couple the two services'
/// availability — Identity down would mean no request could be raised or decided.
/// </para>
/// <para>
/// Instead Identity publishes <c>EmployeeProfileChanged</c>, and this projection
/// is kept current from it. The trade is deliberate: the copy is eventually
/// consistent, so a supervisor reassigned seconds ago may route one request to
/// the previous manager. That is acceptable here because the chain is re-resolved
/// at every decision, so the request corrects itself as it travels.
/// </para>
/// </remarks>
public sealed class EmployeeProjection
{
    private EmployeeProjection()
    {
        Id = string.Empty;
        FullName = string.Empty;
    }

    public string Id { get; private set; }

    public string FullName { get; private set; }

    public string? Email { get; private set; }

    public string? SupervisorId { get; private set; }

    public string? Department { get; private set; }

    public string? Position { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>When the projection last consumed an event for this employee.</summary>
    public DateTimeOffset UpdatedOn { get; private set; }

    public static EmployeeProjection Create(string id, string fullName, DateTimeOffset updatedOn) =>
        new()
        {
            Id = id,
            FullName = fullName,
            IsActive = true,
            UpdatedOn = updatedOn
        };

    public void Apply(
        string fullName,
        string? email,
        string? supervisorId,
        string? department,
        string? position,
        bool isActive,
        DateTimeOffset updatedOn)
    {
        // Events can arrive out of order after a retry. Applying only newer state
        // keeps a redelivered older snapshot from undoing a newer one.
        if (updatedOn < UpdatedOn)
        {
            return;
        }

        FullName = fullName;
        Email = email;
        SupervisorId = supervisorId;
        Department = department;
        Position = position;
        IsActive = isActive;
        UpdatedOn = updatedOn;
    }

    public void Deactivate(DateTimeOffset updatedOn)
    {
        IsActive = false;
        UpdatedOn = updatedOn;
    }
}

public static class DomainErrors
{
    public static readonly Error DescriptionRequired =
        Error.Validation("request.description_required", "A description is required.");

    public static readonly Error SelfRequest =
        Error.Validation("request.self", "You cannot raise a request against yourself.");

    public static readonly Error NoValidationChain =
        Error.Validation(
            "request.no_chain",
            "This employee has no supervisor, so there is nobody to validate the request.");

    public static readonly Error FaultRequired =
        Error.Validation("request.fault_required", "Cite either an existing fault or propose a new one.");

    public static readonly Error AmbiguousFault =
        Error.Validation("request.ambiguous_fault", "Cite an existing fault or propose a new one, not both.");

    public static readonly Error FaultNotFound =
        Error.Validation("request.fault_not_found", "The cited fault does not exist.");

    public static readonly Error EmployeeNotFound =
        Error.Validation("request.employee_not_found", "The chosen employee is not in the directory.");

    public static readonly Error RequestNotFound =
        Error.NotFound("request.not_found", "No such request.");

    public static readonly Error RequestClosed =
        Error.Conflict("request.closed", "This request has already been settled.");

    public static readonly Error NotAwaitingCaller =
        Error.Forbidden("request.not_awaiting_you", "This request is not awaiting your decision.");

    public static readonly Error AlreadyAnswered =
        Error.Conflict("request.already_answered", "You have already answered this request.");

    public static readonly Error NotRequester =
        Error.Forbidden("request.not_requester", "Only the person who raised a request may cancel it.");
}
