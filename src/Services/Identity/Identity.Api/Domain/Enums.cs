namespace Identity.Api.Domain;

/// <summary>Authorisation role carried in the JWT.</summary>
public enum UserRole
{
    /// <summary>Default for a freshly registered user; read-only.</summary>
    Guest = 0,

    /// <summary>May raise sanction requests and answer those addressed to them.</summary>
    Employee = 1,

    /// <summary>May manage users, activate accounts and see every request.</summary>
    Administrator = 2
}

/// <summary>Lifecycle of a user's sign-in account.</summary>
public enum AccountStatus
{
    /// <summary>Registered but not yet approved by an administrator. Cannot sign in.</summary>
    Pending = 0,

    /// <summary>Approved. Can sign in.</summary>
    Active = 1,

    /// <summary>Account withdrawn. Cannot sign in; the employee record is retained.</summary>
    Revoked = 2
}
