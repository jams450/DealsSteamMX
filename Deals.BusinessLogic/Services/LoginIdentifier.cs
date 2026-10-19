namespace Deals.BusinessLogic.Services;

/// <summary>
/// The one normalization for a login identifier, shared by every place that reads <c>users.email</c>
/// from caller input: the credential check, the account lookup behind the lock check, the failure
/// counter and the startup administrator seed.
///
/// It exists because two normalizations used to disagree. <c>UserService.ValidateCredentialsAsync</c>
/// normalized with <c>Trim().ToLowerInvariant()</c> while the account lookup that feeds the lock check
/// compared the raw submitted string, so a caller that respelled the identifier ("User@Example.com ",
/// "user@example.com  ", "USER@EXAMPLE.COM") found no account to lock: the lock check was skipped
/// entirely and the failure counter never incremented, leaving unlimited password attempts against an
/// account whose only brute-force control is that lockout.
///
/// Tied to <c>checks/Auth.Checks</c>: "rule S1: case and surrounding whitespace cannot evade the
/// account lockout" and "rule S1: failures from a respelled identifier count toward the same account".
/// </summary>
public static class LoginIdentifier
{
    /// <summary>
    /// Folds an identifier to its stored form. Absent input folds to an empty string, which no stored
    /// account can match, so a missing identifier can never select a row.
    /// </summary>
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
