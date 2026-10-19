// Deterministic checks for the session-secret placeholder rule (spec S5).
//
// The rule decides whether the BFF refuses to start: a SESSION_SECRET that names itself a placeholder
// encrypts the session cookie with a key anyone can read. The same cases are asserted on the C# side
// (checks/Auth.Checks), so the two implementations cannot drift apart.
//
// Only the pure rule is exercised here: lib/auth/session.ts itself reads process.env and imports
// "server-only", so it cannot be loaded outside the Next runtime. The 32-character minimum is asserted
// where it lives, and the runtime refusal is a documented follow-up.
//
// Run with: node checks/secret-quality/secret-quality-checks.mjs
// A non-zero exit marks the check suite failed.

import { isPlaceholderSecret, MIN_DISTINCT_CHARACTERS } from "../../Deals.Web/lib/security/secret-quality.ts";

const cases = [
  [undefined, true, "an absent secret is a placeholder"],
  ["", true, "an empty secret is a placeholder"],
  ["   ", true, "a blank secret is a placeholder"],
  ["SET_SESSION_SECRET", true, "the repository placeholder convention is a placeholder"],
  ["replace-with-a-long-random-secret", true, "the measured example value is a placeholder"],
  ["change-me-please-0123456789abcdef", true, "a change-me value is a placeholder"],
  ["change me please 0123456789abcdefgh", true, "the same placeholder with spaces instead of hyphens"],
  ["XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", true, "a repeated-character secret is a placeholder"],
  ["a".repeat(32), true, "a single-character secret is a placeholder"],
  ["k7Qv2mNp9Rt4Xw1Zb6Ld3Fs8Gh5Jc0Ya", false, "a varied 32-character secret is accepted"]
];

let failures = 0;

for (const [value, expected, name] of cases) {
  const actual = isPlaceholderSecret(value);
  if (actual !== expected) {
    console.log(`FAIL: ${name} (expected placeholder=${expected}, got ${actual})`);
    failures++;
  }
}

if (MIN_DISTINCT_CHARACTERS !== 8) {
  console.log(`FAIL: the distinct-character floor is ${MIN_DISTINCT_CHARACTERS}, expected 8`);
  failures++;
}

if (failures > 0) {
  process.exitCode = 1;
} else {
  console.log("PASS: session secret placeholder checks");
}
