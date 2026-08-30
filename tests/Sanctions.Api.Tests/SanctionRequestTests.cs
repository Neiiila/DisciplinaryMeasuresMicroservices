using FluentAssertions;
using Sanctions.Api.Domain;

namespace Sanctions.Api.Tests;

/// <summary>
/// The approval chain, which is where the monolith's defects lived.
/// </summary>
public sealed class SanctionRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] Chain = ["SUP1", "SUP2", "SUP3"];

    private static SanctionRequest Raised(IReadOnlyList<string>? chain = null) =>
        SanctionRequest.Raise("EMP1", "REQ1", "Absent without notice", "", faultId: 1, chain ?? Chain, Now).Value;

    // -----------------------------------------------------------------------
    // Raising
    // -----------------------------------------------------------------------

    [Fact]
    public void Raise_routes_to_the_nearest_supervisor_and_requires_the_whole_chain()
    {
        var request = Raised();

        request.CurrentValidatorId.Should().Be("SUP1");
        request.ApprovalsRequired.Should().Be(3);
        request.ApprovalsCollected.Should().Be(0);
        request.IsClosed.Should().BeFalse();
    }

    [Fact]
    public void Raise_rejects_a_request_against_yourself()
    {
        // Otherwise the subject sits in their own approval chain and decides
        // their own sanction.
        var result = SanctionRequest.Raise("EMP1", "EMP1", "d", "", 1, Chain, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("request.self");
    }

    [Fact]
    public void Raise_rejects_an_employee_with_nobody_above_them()
    {
        var result = SanctionRequest.Raise("EMP1", "REQ1", "d", "", 1, [], Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("request.no_chain");
    }

    [Fact]
    public void Raise_requires_a_description()
    {
        SanctionRequest.Raise("EMP1", "REQ1", "   ", "", 1, Chain, Now)
            .Error.Code.Should().Be("request.description_required");
    }

    // -----------------------------------------------------------------------
    // Advancing the chain
    // -----------------------------------------------------------------------

    [Fact]
    public void An_approval_advances_to_the_next_validator()
    {
        var request = Raised();

        request.RecordDecision("SUP1", ValidationDecision.Approved, null, Chain, Now).IsSuccess.Should().BeTrue();

        request.ApprovalsCollected.Should().Be(1);
        request.CurrentValidatorId.Should().Be("SUP2");
        request.IsClosed.Should().BeFalse();
    }

    [Fact]
    public void The_last_approval_closes_the_request()
    {
        var request = Raised();

        foreach (var validator in Chain)
        {
            request.RecordDecision(validator, ValidationDecision.Approved, null, Chain, Now).IsSuccess.Should().BeTrue();
        }

        request.IsClosed.Should().BeTrue();
        request.IsRefused.Should().BeFalse();
        request.CurrentValidatorId.Should().BeNull();
        request.ProgressDisplay.Should().Be("3/3");
    }

    [Fact]
    public void A_refusal_closes_the_request_immediately()
    {
        var request = Raised();

        request.RecordDecision("SUP1", ValidationDecision.Refused, "Not substantiated", Chain, Now);

        request.IsRefused.Should().BeTrue();
        request.IsClosed.Should().BeTrue();
        request.CurrentValidatorId.Should().BeNull();
        request.ApprovalsCollected.Should().Be(0);
    }

    [Fact]
    public void A_missed_step_advances_without_counting_as_an_approval()
    {
        var request = Raised();

        request.RecordDecision("SUP1", ValidationDecision.Missed, null, Chain, Now);

        request.ApprovalsCollected.Should().Be(0);
        request.CurrentValidatorId.Should().Be("SUP2");
        request.IsClosed.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Guards
    // -----------------------------------------------------------------------

    [Fact]
    public void Only_the_validator_the_request_awaits_may_answer()
    {
        var request = Raised();

        var result = request.RecordDecision("SUP2", ValidationDecision.Approved, null, Chain, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("request.not_awaiting_you");
        request.CurrentValidatorId.Should().Be("SUP1");
    }

    /// <summary>
    /// The monolith advanced the counter before running the duplicate check, so a
    /// resubmitted decision inflated progress even though it was rejected.
    /// </summary>
    [Fact]
    public void A_duplicate_decision_is_rejected_without_changing_anything()
    {
        var request = Raised();
        request.RecordDecision("SUP1", ValidationDecision.Approved, null, Chain, Now);

        // SUP1 is no longer the current validator, so the ordering is exercised by
        // a chain where the same person appears twice.
        string[] repeated = ["SUP1", "SUP1"];
        var again = Raised(repeated);
        again.RecordDecision("SUP1", ValidationDecision.Approved, null, repeated, Now);

        var duplicate = again.RecordDecision("SUP1", ValidationDecision.Approved, null, repeated, Now);

        duplicate.IsFailure.Should().BeTrue();
        again.ApprovalsCollected.Should().Be(1);
    }

    [Fact]
    public void A_settled_request_accepts_no_further_decisions()
    {
        var request = Raised();
        request.RecordDecision("SUP1", ValidationDecision.Refused, null, Chain, Now);

        var result = request.RecordDecision("SUP2", ValidationDecision.Approved, null, Chain, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("request.closed");
    }

    /// <summary>
    /// The chain is supplied fresh at every decision rather than stored, so a
    /// reorganisation between two steps is honoured as the request travels.
    /// </summary>
    [Fact]
    public void The_chain_is_re_resolved_at_each_decision()
    {
        var request = Raised();
        request.RecordDecision("SUP1", ValidationDecision.Approved, null, Chain, Now);
        request.CurrentValidatorId.Should().Be("SUP2");

        // SUP3 has since left and SUP9 sits above SUP2 instead. The next step
        // follows the chain as it is now, not as it was when the request was
        // raised.
        string[] reorganised = ["SUP1", "SUP2", "SUP9"];
        request.RecordDecision("SUP2", ValidationDecision.Approved, null, reorganised, Now)
            .IsSuccess.Should().BeTrue();

        request.CurrentValidatorId.Should().Be("SUP9");
    }

    [Fact]
    public void A_validator_missing_from_the_new_chain_closes_the_request_rather_than_stranding_it()
    {
        var request = Raised();

        // SUP1 answers, but the freshly resolved chain no longer contains them, so
        // there is no position to advance from.
        request.RecordDecision("SUP1", ValidationDecision.Approved, null, ["SUP7", "SUP8"], Now);

        request.IsClosed.Should().BeTrue();
        request.CurrentValidatorId.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // Cancellation
    // -----------------------------------------------------------------------

    [Fact]
    public void The_requester_may_cancel_while_the_request_is_open()
    {
        var request = Raised();

        request.Cancel("REQ1").IsSuccess.Should().BeTrue();

        request.IsCancelled.Should().BeTrue();
        request.IsClosed.Should().BeTrue();
        request.CurrentValidatorId.Should().BeNull();
    }

    [Fact]
    public void Nobody_else_may_cancel()
    {
        var request = Raised();

        request.Cancel("SUP1").Error.Code.Should().Be("request.not_requester");
        request.IsCancelled.Should().BeFalse();
    }

    [Fact]
    public void A_settled_request_cannot_be_cancelled()
    {
        var request = Raised();
        request.RecordDecision("SUP1", ValidationDecision.Refused, null, Chain, Now);

        request.Cancel("REQ1").Error.Code.Should().Be("request.closed");
    }
}
