using FluentAssertions;
using Identity.Api.Domain;
using Identity.Api.Infrastructure;

namespace Identity.Api.Tests;

public sealed class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static User NewUser() =>
        User.CreateEmployee("EMP1", PersonName.Create("Amina", "Haddad"), UserRole.Employee, Now);

    [Fact]
    public void A_new_employee_has_no_account()
    {
        var user = NewUser();

        user.HasAccount.Should().BeFalse();
        user.PasswordHash.Should().BeNull();
    }

    /// <summary>
    /// Registration must never grant access on its own; an administrator has to
    /// activate the account first.
    /// </summary>
    [Fact]
    public void Opening_an_account_leaves_it_pending()
    {
        var user = NewUser();

        user.OpenAccount("amina@company.com", "hash", UserRole.Employee).IsSuccess.Should().BeTrue();

        user.AccountStatus.Should().Be(AccountStatus.Pending);
        user.EnsureCanSignIn().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void A_pending_account_says_why_it_cannot_sign_in()
    {
        var user = NewUser();
        user.OpenAccount("amina@company.com", "hash", UserRole.Employee);

        user.EnsureCanSignIn().Error.Code.Should().Be("auth.awaiting_activation");
    }

    [Fact]
    public void An_activated_account_may_sign_in()
    {
        var user = NewUser();
        user.OpenAccount("amina@company.com", "hash", UserRole.Employee);

        user.Activate().IsSuccess.Should().BeTrue();

        user.AccountStatus.Should().Be(AccountStatus.Active);
        user.EnsureCanSignIn().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_revoked_account_may_not_sign_in()
    {
        var user = NewUser();
        user.OpenAccount("amina@company.com", "hash", UserRole.Employee);
        user.Activate();

        user.RevokeAccount().IsSuccess.Should().BeTrue();

        user.EnsureCanSignIn().Error.Code.Should().Be("auth.revoked");
    }

    [Fact]
    public void A_second_account_cannot_be_opened_on_the_same_user()
    {
        var user = NewUser();
        user.OpenAccount("amina@company.com", "hash", UserRole.Employee);

        user.OpenAccount("other@company.com", "hash2", UserRole.Administrator)
            .Error.Code.Should().Be("user.account_exists");
    }

    [Fact]
    public void Activation_requires_an_account_to_activate()
    {
        NewUser().Activate().Error.Code.Should().Be("user.no_account");
    }

    /// <summary>
    /// A user supervising themselves would produce a validation chain that never
    /// terminates, so the request would route to its own subject forever.
    /// </summary>
    [Fact]
    public void A_user_cannot_supervise_themselves()
    {
        var user = NewUser();

        user.AssignSupervisor("EMP1").Error.Code.Should().Be("user.self_supervision");
        user.SupervisorId.Should().BeNull();
    }

    [Fact]
    public void Self_supervision_is_rejected_regardless_of_casing()
    {
        NewUser().AssignSupervisor("emp1").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Clearing_the_supervisor_is_allowed()
    {
        var user = NewUser();
        user.AssignSupervisor("EMP2");

        user.AssignSupervisor("   ").IsSuccess.Should().BeTrue();

        user.SupervisorId.Should().BeNull();
    }

    [Fact]
    public void Soft_delete_hides_the_user_and_withdraws_access()
    {
        var user = NewUser();
        user.OpenAccount("amina@company.com", "hash", UserRole.Employee);
        user.Activate();

        user.SoftDelete();

        user.IsDeleted.Should().BeTrue();
        user.AccountStatus.Should().Be(AccountStatus.Revoked);
        user.EnsureCanSignIn().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void A_deleted_user_reports_generic_invalid_credentials()
    {
        // Distinguishing "deleted" from "wrong password" at sign-in would tell an
        // attacker which matriculation numbers once existed.
        var user = NewUser();
        user.OpenAccount("amina@company.com", "hash", UserRole.Employee);
        user.Activate();
        user.SoftDelete();

        user.EnsureCanSignIn().Error.Code.Should().Be("auth.invalid_credentials");
    }
}

public sealed class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void A_password_verifies_against_its_own_hash()
    {
        var hash = _hasher.Hash("correct horse battery");

        _hasher.Verify(hash, "correct horse battery").Should().BeTrue();
    }

    [Fact]
    public void A_wrong_password_does_not_verify()
    {
        var hash = _hasher.Hash("correct horse battery");

        _hasher.Verify(hash, "correct horse batter").Should().BeFalse();
    }

    /// <summary>
    /// Per-password salt: identical passwords must not produce identical hashes,
    /// or a single rainbow table breaks every account that shares one.
    /// </summary>
    [Fact]
    public void The_same_password_hashes_differently_each_time()
    {
        _hasher.Hash("same").Should().NotBe(_hasher.Hash("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("1.2")]
    [InlineData("notanumber.c2FsdA==.aGFzaA==")]
    public void A_malformed_stored_hash_fails_closed(string storedHash)
    {
        // Anything unparseable must be rejected, never treated as a match.
        _hasher.Verify(storedHash, "anything").Should().BeFalse();
    }
}
