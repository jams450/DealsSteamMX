using System.Diagnostics.CodeAnalysis;

namespace Deals.API.Security;

/// <summary>
/// The one placeholder rule for secrets. It exists because a guard that only looked for the repository's
/// own <c>SET_</c> convention accepted a value that names itself a placeholder: <c>.env.example</c> shipped
/// <c>Jwt__Key=replace-with-a-long-random-secret</c> - 33 characters - which passed "at least 32 bytes and
/// not <c>SET_</c>", and that key would have signed tokens anyone could forge, administrator claims
/// included. The example also carried 25-character repeated placeholders, which the length rule does catch
/// but only by accident of their length.
///
/// The rule is deliberately a placeholder rule, not a strength rule: it answers "is this a value someone
/// is supposed to replace", which is why it is safe to fail closed. Entropy and length are separate
/// questions, answered where each secret is validated.
///
/// Mirrored in <c>Deals.Web/lib/security/secret-quality.ts</c>; both sides are pinned by checks
/// (<c>checks/Auth.Checks</c> and <c>checks/secret-quality</c>) over the same cases, so a change here that
/// is not made there fails.
/// </summary>
public static class SecretPlaceholder
{
    private static readonly string[] Prefixes =
        ["SET_", "CHANGE_ME", "CHANGEME", "REPLACE", "TODO", "YOUR_", "XXX"];

    /// <summary>
    /// Below this many distinct characters a value is a repeated placeholder rather than a secret
    /// ("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"). A thirty-two character secret drawn from any realistic
    /// alphabet has far more, so this only rejects values that were never meant to be one.
    /// </summary>
    public const int MinDistinctCharacters = 8;

    /// <summary>
    /// True when the value is absent, names itself a placeholder, or repeats one character.
    /// Annotated so a caller can use the negative result to prove the local is non-null.
    /// </summary>
    public static bool IsPlaceholder([NotNullWhen(false)] string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        var probe = StripSeparators(trimmed);

        foreach (var prefix in Prefixes)
        {
            if (probe.StartsWith(StripSeparators(prefix), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return trimmed.Distinct().Count() < MinDistinctCharacters;
    }

    /// <summary>
    /// "change-me", "change_me" and "change me" are the same placeholder, so separators are dropped before
    /// matching: a guard that knew only one spelling was walked around by punctuating the value differently.
    /// The distinct-character count deliberately runs on the value as written, not on this probe.
    /// </summary>
    private static string StripSeparators(string value) =>
        string.Concat(value.Where(character => character is not ('-' or '_' or ' ')));
}
