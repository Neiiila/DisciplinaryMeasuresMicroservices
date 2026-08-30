using BuildingBlocks.Core.Results;

namespace Identity.Api.Domain;

/// <summary>
/// Every expected failure this service can produce, in one place.
/// </summary>
/// <remarks>
/// Codes are the stable half of the contract and clients branch on them; messages
/// are display text and may be reworded freely.
/// </remarks>
public static class DomainErrors
{
    // Sign-in. The generic message is deliberate: distinguishing "no such user"
    // from "wrong password" tells an attacker which matriculation numbers exist.
    public static readonly Error InvalidCredentials =
        Error.Unauthorized("auth.invalid_credentials", "The identifier or password is incorrect.");

    public static readonly Error AccountAwaitingActivation =
        Error.Forbidden("auth.awaiting_activation", "This account is awaiting activation by an administrator.");

    public static readonly Error AccountRevoked =
        Error.Forbidden("auth.revoked", "This account has been revoked.");

    public static readonly Error MissingIdentifier =
        Error.Validation("auth.missing_identifier", "Supply either a matriculation number or an email address.");

    // Accounts
    public static readonly Error AccountAlreadyOpen =
        Error.Conflict("user.account_exists", "This user already has a sign-in account.");

    public static readonly Error NoAccountToActivate =
        Error.Conflict("user.no_account", "This user has no sign-in account to activate.");

    public static readonly Error NoAccountToRevoke =
        Error.Conflict("user.no_account", "This user has no sign-in account to revoke.");

    // Records
    public static readonly Error UserNotFound =
        Error.NotFound("user.not_found", "No such user.");

    public static readonly Error IdTaken =
        Error.Conflict("user.id_taken", "That matriculation number is already in use.");

    public static readonly Error EmailTaken =
        Error.Conflict("user.email_taken", "That email address is already in use.");

    public static readonly Error SelfSupervision =
        Error.Validation("user.self_supervision", "A user cannot be their own supervisor.");

    public static readonly Error SupervisorNotFound =
        Error.Validation("user.supervisor_not_found", "The chosen supervisor does not exist.");

    public static readonly Error WeakPassword =
        Error.Validation("user.weak_password", "The password must be at least 8 characters long.");
}
