/**
 * The placeholder rule for the session secret, mirrored from the API's `SecretPlaceholder` so the two
 * sides cannot disagree about what "configured" means. A SESSION_SECRET that names itself a placeholder
 * encrypts the session cookie with a key anyone can read, which is worse than not starting.
 *
 * It is a placeholder rule, not a strength rule: it answers "is this a value someone is supposed to
 * replace", which is why it is safe to fail closed. The 32-character minimum lives with the secret in
 * `lib/auth/session.ts`.
 *
 * Pinned by `checks/secret-quality/secret-quality-checks.mjs` over the same cases as the C# side
 * (`checks/Auth.Checks`), so changing one without the other fails.
 */
const PLACEHOLDER_PREFIXES = ["set_", "change_me", "changeme", "replace", "todo", "your_", "xxx"];

// "change-me", "change_me" and "change me" are the same placeholder: separators are dropped before
// matching, so a value cannot slip through by punctuating it differently.
const stripSeparators = (value: string): string => value.replace(/[-_ ]/g, "");

export const MIN_DISTINCT_CHARACTERS = 8;

export function isPlaceholderSecret(value: string | undefined): boolean {
  if (!value || value.trim().length === 0) {
    return true;
  }

  const trimmed = value.trim();
  const probe = stripSeparators(trimmed).toLowerCase();

  if (PLACEHOLDER_PREFIXES.some((prefix) => probe.startsWith(stripSeparators(prefix)))) {
    return true;
  }

  return new Set(trimmed).size < MIN_DISTINCT_CHARACTERS;
}
