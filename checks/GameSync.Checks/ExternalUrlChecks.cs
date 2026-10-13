#nullable enable
using System.Reflection;
using Deals.BusinessLogic.Services;

/// <summary>
/// Pins which provider-supplied URLs are allowed to reach the UI as an offer link. The production method is
/// private and this repo has no test project, so it is invoked through reflection: the check stays
/// deterministic without widening the public surface of <see cref="SteamGameService"/> just for a test.
/// </summary>
internal static class ExternalUrlChecks
{
    private static readonly MethodInfo Method = typeof(SteamGameService)
        .GetMethod("ExternalUrlForSource", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SteamGameService.ExternalUrlForSource not found");

    private static string? Allowed(string source, string? value) =>
        (string?)Method.Invoke(null, [source, value]);

    public static void Run()
    {
        static void Check(bool ok, string name)
        {
            if (!ok) throw new Exception(name);
        }

        // ITAD's own offer URL is its affiliate redirector (`itad.link`), which lands on the store page.
        // Keeping only isthereanydeal.com drops the link from those rows while the price still renders, so
        // the row shows a price the user cannot follow.
        const string redirector = "https://itad.link/abc123";
        Check(Allowed("itad", redirector) == redirector, "ITAD redirector link is kept");

        // The store attribution page URLs ITAD already returned must keep working unchanged.
        const string itadPage = "https://isthereanydeal.com/deal/x/";
        Check(Allowed("itad", itadPage) == itadPage, "isthereanydeal.com link is kept");
        Check(Allowed("itad", "https://www.isthereanydeal.com/deal/x/") is not null,
            "isthereanydeal.com subdomain is kept");

        // The same two rules ItadClient.TryReadRedirectorUrl applies: HTTPS only, and the exact redirector
        // host (a look-alike or a subdomain of itad.link is not the redirector that method follows).
        Check(Allowed("itad", "http://itad.link/abc123") is null, "non-HTTPS ITAD link is rejected");
        Check(Allowed("itad", "https://sub.itad.link/abc123") is null,
            "itad.link subdomain is not the redirector");
        Check(Allowed("itad", "https://itad.link.evil.example/x") is null,
            "look-alike host is not the redirector");
        Check(Allowed("itad", "https://evil.example/x") is null, "foreign host is rejected");

        // The redirector belongs to ITAD: another provider claiming it must not slip through.
        Check(Allowed("ggdeals", redirector) is null, "redirector is only allowed for ITAD");
        Check(Allowed("itad", null) is null, "missing link stays missing");

        Console.WriteLine("PASS: external offer URL checks");
    }
}
