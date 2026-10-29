#nullable enable
using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Models.Bundles;

/// <summary>
/// The pure rules of the red bundle reminder (spec S2): the 3-day window with its exact boundary, the dedup
/// identity (user + source family + bundle key + expiry instant), the source-family rule and the sanitized
/// message. No DB, no HTTP, no clock of its own.
/// </summary>
internal static class BundleAlertPolicyChecks
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    public static void Run()
    {
        WindowBoundary();
        RemainingDays();
        SourceFamily();
        DedupIdentity();
        Message();
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
    }

    /// <summary>
    /// S1/S2: red is "3 days or less", so a bundle that expires in exactly 72 hours still is red and one that
    /// expires in 73 hours is not. An expired or undated bundle is never red.
    /// </summary>
    private static void WindowBoundary()
    {
        Check(BundleAlertPolicy.IsWithinAlertWindow(Now.AddHours(72), Now),
            "exactly 72 hours is inside the red window");
        Check(!BundleAlertPolicy.IsWithinAlertWindow(Now.AddHours(72).AddSeconds(1), Now),
            "72 hours and one second is outside the red window");
        Check(BundleAlertPolicy.IsWithinAlertWindow(Now.AddHours(71).AddMinutes(59), Now),
            "71h59m is inside the red window");
        Check(BundleAlertPolicy.IsWithinAlertWindow(Now.AddSeconds(1), Now),
            "one second of life is inside the red window");
        Check(BundleAlertPolicy.IsWithinAlertWindow(Now.AddDays(1), Now),
            "24 hours is inside the red window");
        Check(!BundleAlertPolicy.IsWithinAlertWindow(Now, Now),
            "an expiry equal to now is already expired, so it is not red");
        Check(!BundleAlertPolicy.IsWithinAlertWindow(Now.AddSeconds(-1), Now),
            "an expired bundle is not red");
        Check(!BundleAlertPolicy.IsWithinAlertWindow(null, Now),
            "a bundle with no expiry date is never red");
        Check(!BundleAlertPolicy.IsWithinAlertWindow(Now.AddDays(30), Now),
            "a bundle 30 days away is not red");
    }

    /// <summary>The day count the message shows is the same ceil the badge uses: 23 hours is "1 day".</summary>
    private static void RemainingDays()
    {
        Check(BundleAlertPolicy.DaysRemaining(Now.AddHours(72), Now) == 3, "72 hours is 3 days");
        Check(BundleAlertPolicy.DaysRemaining(Now.AddHours(72).AddSeconds(1), Now) == 4,
            "72h01s is 4 days, and that is why it is not red");
        Check(BundleAlertPolicy.DaysRemaining(Now.AddHours(23), Now) == 1, "23 hours is 1 day");
        Check(BundleAlertPolicy.DaysRemaining(Now.AddSeconds(1), Now) == 1, "one second is 1 day");
        Check(BundleAlertPolicy.DaysRemaining(Now, Now) is null, "an expiry equal to now has no remaining day");
        Check(BundleAlertPolicy.DaysRemaining(Now.AddDays(-1), Now) is null, "an expired bundle has no remaining day");
        Check(BundleAlertPolicy.DaysRemaining(null, Now) is null, "an undated bundle has no remaining day");
    }

    /// <summary>
    /// itad and itad_global are the same provider reporting the same bundle, so they share a family; gg.deals
    /// is its own key space. The family must agree with the purchase rule that hides a bundle from the pending
    /// catalog, otherwise a purchase would hide a card whose reminder still fires (or the reverse).
    /// </summary>
    private static void SourceFamily()
    {
        Check(BundleAlertPolicy.SourceFamily("itad") == "itad", "itad is its own family");
        Check(BundleAlertPolicy.SourceFamily("itad_global") == "itad", "itad_global belongs to the itad family");
        Check(BundleAlertPolicy.SourceFamily("ggdeals") == "ggdeals", "gg.deals is its own family");

        foreach (var left in new[] { "itad", "itad_global", "ggdeals" })
        {
            foreach (var right in new[] { "itad", "itad_global", "ggdeals" })
            {
                var sameFamily =
                    BundleAlertPolicy.SourceFamily(left) == BundleAlertPolicy.SourceFamily(right);
                var hidden = BundlePurchaseRules.HidesBundle(left, "bundle-1", right, "bundle-1");
                Check(sameFamily == hidden,
                    $"the alert family of {left}/{right} must match the purchase hiding rule");
            }
        }
    }

    /// <summary>
    /// One alert per user, source family, bundle key and expiry instant: a repeated job run and a second
    /// provider source of the same family collapse into one key, while gg.deals and a new expiry are new cycles.
    /// </summary>
    private static void DedupIdentity()
    {
        var expiry = Now.AddDays(2);
        var key = BundleAlertPolicy.DedupKey(7, "itad", "bundle-1", expiry);

        Check(key == BundleAlertPolicy.DedupKey(7, "itad", "bundle-1", expiry),
            "the same bundle and expiry produce the same key");
        Check(key == BundleAlertPolicy.DedupKey(7, "itad_global", "bundle-1", expiry),
            "the two ITAD sources share one alert per bundle");
        Check(key != BundleAlertPolicy.DedupKey(7, "ggdeals", "bundle-1", expiry),
            "gg.deals is never collapsed with an ITAD bundle");
        Check(key != BundleAlertPolicy.DedupKey(8, "itad", "bundle-1", expiry),
            "the key is per operator account");
        Check(key != BundleAlertPolicy.DedupKey(7, "itad", "bundle-2", expiry),
            "two bundles are two keys, even when their titles are equal");
        Check(key != BundleAlertPolicy.DedupKey(7, "itad", "bundle-1", expiry.AddHours(1)),
            "a new expiry is a new reminder cycle");
        Check(key.Length <= BundleAlertEventDedupKeyMaxLength,
            "the key fits the outbox column for the longest bundle key");
        Check(BundleAlertPolicy.DedupKey(7, "itad", new string('k', 128), DateTime.SpecifyKind(expiry, DateTimeKind.Local)).Length
                <= BundleAlertEventDedupKeyMaxLength,
            "a local-Kind expiry still fits and is normalized, not dropped");
    }

    /// <summary>Longest value the outbox supports, mirroring the offer outbox (VARCHAR(320)).</summary>
    private const int BundleAlertEventDedupKeyMaxLength = 320;

    /// <summary>
    /// The message names the bundle, the explicit remaining days and the expiry instant, and its click target
    /// is an HTTPS URL or nothing at all: a non-HTTPS provider URL is dropped instead of being pushed.
    /// </summary>
    private static void Message()
    {
        var bundle = new BundleAlertCandidate(
            "itad",
            "bundle-1",
            "Prestige Collection",
            "Fanatical",
            "https://isthereanydeal.com/bundle/1",
            "https://www.fanatical.com/es/bundle/1?ref=itad",
            Now.AddHours(50),
            Now);

        var message = BundleAlertPolicy.BuildMessage(bundle, Now);

        Check(message.Title.Contains("Prestige Collection", StringComparison.Ordinal),
            "the title names the bundle");
        Check(message.Body.Contains("Prestige Collection", StringComparison.Ordinal), "the body names the bundle");
        Check(message.Body.Contains("3 días", StringComparison.Ordinal),
            "the body states the explicit ceil day count, never a colour");
        Check(message.Body.Contains("Fanatical", StringComparison.Ordinal), "the body names the shop when known");
        Check(message.Body.Contains("2026-10-24", StringComparison.Ordinal),
            "the body carries the expiry instant in UTC");
        Check(message.ClickUrl == "https://www.fanatical.com/es/bundle/1?ref=itad",
            "the affiliate deal URL is preserved verbatim as the click target");

        var noDealUrl = BundleAlertPolicy.BuildMessage(bundle with { DealUrl = null }, Now);
        Check(noDealUrl.ClickUrl == "https://isthereanydeal.com/bundle/1",
            "the bundle page is the fallback click target");

        var insecure = BundleAlertPolicy.BuildMessage(
            bundle with { DealUrl = "http://www.fanatical.com/es/bundle/1", PageUrl = "javascript:alert(1)" },
            Now);
        Check(insecure.ClickUrl is null, "a non-HTTPS URL is dropped, never pushed");

        var noShop = BundleAlertPolicy.BuildMessage(bundle with { ShopName = "  " }, Now);
        Check(!noShop.Body.Contains("Tienda:", StringComparison.Ordinal),
            "an unknown shop adds no dangling label");
        Check(noShop.Body.Length <= 1024, "the body fits the outbox column");

        var hostile = new BundleAlertCandidate(
            "itad",
            "bundle-2",
            "Evil\r\nTitle\u0007",
            "Shop\nName",
            null,
            "https://example.com/b\u0000undle",
            Now.AddHours(4),
            Now);
        var sanitized = BundleAlertPolicy.BuildMessage(hostile, Now);
        Check(!sanitized.Title.Contains('\n') && !sanitized.Title.Contains('\r'),
            "control characters never reach the ntfy headers");
        Check(!sanitized.Body.Contains('\n') && !sanitized.Body.Contains('\r'),
            "control characters never reach the ntfy body");
        Check(sanitized.ClickUrl is null || !sanitized.ClickUrl.Contains('\u0000'),
            "a control character in the URL drops it instead of sending it");
        Check(sanitized.Title.Length <= 512, "the title fits the outbox column");

        var longTitle = new string('x', 900);
        var truncated = BundleAlertPolicy.BuildMessage(bundle with { Title = longTitle }, Now);
        Check(truncated.Title.Length <= 512, "an oversized title is truncated, not rejected");
        Check(truncated.Body.Length <= 1024, "an oversized body is truncated, not rejected");
    }
}
