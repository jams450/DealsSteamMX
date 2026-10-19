using System.Collections;
using System.Net;
using System.Reflection;
using AuthChecks;
using Deals.API;
using Deals.API.Extensions;
using Deals.API.Models.Auth;
using Deals.API.Security;
using Deals.API.Services;
using Microsoft.Extensions.Configuration;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// Deterministic checks for the account-lockout rule (spec S1).
//
// Rule: the identifier a caller submits must resolve to the same stored account whatever its spelling
// (letter case and surrounding whitespace), so neither the lock check nor the failure counter can be
// evaded by respelling the login. The rule is observed only through IAuthService.AuthenticateAsync:
// nothing here reaches into the counter, the lock column or the row count.
//
// Why the in-memory provider: the rule lives in AuthService, which needs a DbContext, and this
// checkout has no reachable PostgreSQL. Every fixture is synthetic and no credential is real.
//
// A non-zero exit or a thrown exception marks the check suite failed.

const int maxFailedAttempts = 3;
const string correctPassword = "correct-horse-battery-staple";

var failures = new List<string>();
void Check(bool value, string name)
{
    if (!value) failures.Add(name);
}

var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
    .UseInMemoryDatabase($"auth-checks-{Guid.NewGuid():N}")
    .Options;

await using var context = new AppDbContext(dbOptions, new StubCurrentUserService());
var passwords = new PasswordService();

context.Users.AddRange(
    new User
    {
        UserId = 1, Name = "Control", Email = "control@example.com", Active = true, SessionVersion = 1,
        Password = passwords.HashPassword(correctPassword)
    },
    new User
    {
        UserId = 2, Name = "Variant", Email = "variant@example.com", Active = true, SessionVersion = 1,
        Password = passwords.HashPassword(correctPassword)
    },
    new User
    {
        UserId = 3, Name = "Fresh", Email = "fresh@example.com", Active = true, SessionVersion = 1,
        Password = passwords.HashPassword(correctPassword)
    },
    new User
    {
        UserId = 4, Name = "Mixed", Email = "mixed@example.com", Active = true, SessionVersion = 1,
        Password = passwords.HashPassword(correctPassword)
    });
await context.SaveChangesAsync();

var auth = new AuthService(
    new StubJwtService(),
    new UserService(new InMemoryRepository(context), passwords),
    context,
    Options.Create(new AuthOptions
    {
        MaxFailedAttempts = maxFailedAttempts,
        LockMinutes = 15,
        RefreshDays = 30
    }));

async Task AttemptAsync(string identifier, string password)
{
    await auth.AuthenticateAsync(new LoginRequest(identifier, password));
}

// Control: with the stored spelling the harness must be able to observe a lock. If this ever stops
// failing, the suite itself is broken and every other check below is meaningless.
for (var attempt = 0; attempt < maxFailedAttempts; attempt++)
{
    await AttemptAsync("control@example.com", "wrong-password");
}

Check(await auth.AuthenticateAsync(new LoginRequest("control@example.com", correctPassword)) is null,
    "control: the stored spelling locks the account after MaxFailedAttempts failures");

// Rule S1, first observable effect: respelling the identifier must not walk around the lock.
for (var attempt = 0; attempt < maxFailedAttempts; attempt++)
{
    await AttemptAsync("  VaRiAnT@Example.COM  ", "wrong-password");
}

Check(await auth.AuthenticateAsync(new LoginRequest("variant@example.com", correctPassword)) is null,
    "rule S1: case and surrounding whitespace cannot evade the account lockout");

// Rule S1, second observable effect: failures must accumulate on one account even when the caller
// alternates spellings, otherwise the threshold is never reached.
await AttemptAsync("mixed@example.com", "wrong-password");
await AttemptAsync("mixed@example.com", "wrong-password");
await AttemptAsync("MIXED@example.com ", "wrong-password");

Check(await auth.AuthenticateAsync(new LoginRequest("mixed@example.com", correctPassword)) is null,
    "rule S1: failures from a respelled identifier count toward the same account");

// Existing behavior that must survive the fix: an account that has not reached the threshold still
// authenticates, and a correct password still works from a differently spelled identifier.
Check(await auth.AuthenticateAsync(new LoginRequest("  FRESH@example.com  ", correctPassword)) is not null,
    "existing behavior: a differently spelled identifier still authenticates with the correct password");

// Trusted-proxy decision (spec S2). Processing X-Forwarded-For is what makes the auth rate limiter fair
// behind a proxy, and trusting it without naming the chain is what turns that limiter into a spoofable
// no-op: the caller would choose its own partition. Both directions are asserted, plus the two loud
// failures.
var untrustedByDefault = TrustedProxySettings.Resolve(false, null);
Check(!untrustedByDefault.Enabled && untrustedByDefault.KnownProxies.Count == 0,
    "trusted-proxy: the default configuration trusts nothing");

var listedButNotTrusted = TrustedProxySettings.Resolve(false, ["10.0.0.5"]);
Check(!listedButNotTrusted.Enabled,
    "trusted-proxy: naming an address does not by itself enable trust");

var trusted = TrustedProxySettings.Resolve(true, ["10.0.0.5", " 10.0.0.6 "]);
Check(trusted.Enabled &&
      trusted.KnownProxies.SequenceEqual([IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.6")]),
    "trusted-proxy: a declared chain is trusted and its entries are trimmed");

Check(RaisesStartupError(() => TrustedProxySettings.Resolve(true, [])),
    "trusted-proxy: trust requested without a named proxy is refused at startup");

Check(RaisesStartupError(() => TrustedProxySettings.Resolve(true, ["not-an-address"])),
    "trusted-proxy: a malformed proxy address is refused instead of silently dropped");

// The documented configuration shape has to work. Proxy__KnownProxies is a comma-separated leaf, which is a
// single configuration value with no children: read as a list it yields nothing at all, and a chain that was
// declared would be refused at startup - which is what the first version of this did.
var commaForm = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Proxy:TrustForwardedHeaders"] = "true",
        ["Proxy:KnownProxies"] = "10.0.0.5, 10.0.0.6"
    })
    .Build();
var commaSettings = TrustedProxySettings.FromConfiguration(commaForm.GetSection("Proxy"));
Check(commaSettings.Enabled &&
      commaSettings.KnownProxies.SequenceEqual([IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.6")]),
    "trusted-proxy: the documented comma-separated value names both proxies");

var listForm = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Proxy:TrustForwardedHeaders"] = "true",
        ["Proxy:KnownProxies:0"] = "10.0.0.5",
        ["Proxy:KnownProxies:1"] = "10.0.0.6"
    })
    .Build();
var listSettings = TrustedProxySettings.FromConfiguration(listForm.GetSection("Proxy"));
Check(listSettings.Enabled && listSettings.KnownProxies.Count == 2,
    "trusted-proxy: the list form names both proxies");

var absentForm = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
Check(!TrustedProxySettings.FromConfiguration(absentForm.GetSection("Proxy")).Enabled,
    "trusted-proxy: an absent section trusts nothing");

Check(RaisesStartupError(() => TrustedProxySettings.Resolve(true, ["10.0.0.5,nonsense"])),
    "trusted-proxy: one malformed entry inside the comma form is refused");

// Secret placeholders (spec S5). The guard that only looked for the repository's SET_ convention accepted
// Jwt__Key=replace-with-a-long-random-secret - 33 characters - which would have signed forgeable tokens.
// The same cases are asserted on the TypeScript side (checks/secret-quality).
Check(SecretPlaceholder.IsPlaceholder("replace-with-a-long-random-secret"),
    "secret: the shipped example Jwt__Key is a placeholder");

Check(SecretPlaceholder.IsPlaceholder("SET_ITAD_API_KEY") &&
      SecretPlaceholder.IsPlaceholder("change-me-please-0123456789abcdef") &&
      SecretPlaceholder.IsPlaceholder("change me please 0123456789abcdefgh"),
    "secret: the repository placeholder convention is a placeholder in any of its spellings");

Check(SecretPlaceholder.IsPlaceholder(null) && SecretPlaceholder.IsPlaceholder("   "),
    "secret: an absent secret is a placeholder");

Check(SecretPlaceholder.IsPlaceholder(new string('X', 32)) &&
      SecretPlaceholder.IsPlaceholder(new string('a', 32)),
    "secret: a repeated-character secret is a placeholder whatever its length");

Check(!SecretPlaceholder.IsPlaceholder("k7Qv2mNp9Rt4Xw1Zb6Ld3Fs8Gh5Jc0Ya"),
    "secret: a varied 32-character secret is accepted");

Check(SecretPlaceholder.MinDistinctCharacters == 8,
    "secret: the distinct-character floor matches the mirrored rule");

// The rate-limit split (spec S2), asserted against the policies the API actually registers rather than
// against the source text: one shared bucket is what let a login flood consume every user's refresh budget
// and log everybody out. RateLimiterOptions keeps its policy map out of the public surface, so it is read by
// reflection - and an unreachable map fails this check loudly instead of passing silently.
var rateLimitServices = new ServiceCollection();
rateLimitServices.AddApiRateLimiting();
using var rateLimitProvider = rateLimitServices.BuildServiceProvider();
var rateLimiterOptions = rateLimitProvider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
var policyMap = ReadPolicyMap(rateLimiterOptions);

Check(policyMap is IDictionary,
    "rate-limit: the configured policy map is reachable from the check");

if (policyMap is IDictionary map)
{
    var policyNames = map.Keys
        .Cast<object>()
        .Select(key => key.ToString() ?? string.Empty)
        .ToHashSet(StringComparer.Ordinal);

    Check(policyNames.Contains("auth-login") && policyNames.Contains("auth-refresh"),
        $"rate-limit: login and refresh are separate policies (registered: {string.Join(", ", policyNames.Order(StringComparer.Ordinal))})");

    Check(!policyNames.Contains("auth"),
        "rate-limit: the single shared auth bucket is gone");
}

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.WriteLine($"FAIL: {failure}");
    }

    return 1;
}

Console.WriteLine("PASS: login identification, account lockout, trusted-proxy and secret-placeholder checks");
return 0;

static bool RaisesStartupError(Action action)
{
    try
    {
        action();
        return false;
    }
    catch (InvalidOperationException)
    {
        return true;
    }
}

// RateLimiterOptions exposes AddPolicy but not the resulting map, so the check reads it directly. Both
// shapes are tried: if neither exists, the caller reports a failed check rather than a green suite.
static object? ReadPolicyMap(RateLimiterOptions options)
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    var type = typeof(RateLimiterOptions);

    return type.GetProperty("PolicyMap", Flags)?.GetValue(options)
        ?? type.GetField("PolicyMap", Flags)?.GetValue(options);
}
