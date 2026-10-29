using System.Globalization;
using System.Text;
using Deals.BusinessLogic.Models.Bundles;

namespace Deals.API.Services.Notifications;

/// <summary>
/// One active bundle inside the red window, flattened for the reminder policy. <see cref="ExpiresAt"/> is the
/// persisted <c>external_bundles.expires_at</c> and is non-nullable because a bundle without a date never becomes
/// a candidate: there is no cycle to count down.
/// </summary>
public sealed record BundleAlertCandidate(
    string Source,
    string BundleKey,
    string Title,
    string? ShopName,
    string? PageUrl,
    string? DealUrl,
    DateTime ExpiresAt,
    DateTime ObservedAt);

/// <summary>Ready-to-send text of one reminder. Built once by <see cref="BundleAlertPolicy.BuildMessage"/>.</summary>
public sealed record BundleAlertMessage(string Title, string Body, string? ClickUrl);

/// <summary>
/// Pure, deterministic policy for the red bundle reminders (spec S2). No DB, no HTTP, no clock of its own: the
/// caller supplies the bundle snapshot and the evaluation instant, which is what makes every rule checkable.
///
/// <para>
/// "Rojo" is the same definition the badge uses (spec S1,
/// <c>Deals.Web/app/bundles/_lib/bundle-expiry.ts</c>): remaining days are
/// <c>ceil((expires_at - now) / 24h)</c> and red is <b>3 days or fewer</b>. The boundary is therefore
/// <c>expiry &gt; now &amp;&amp; expiry &lt;= now + 72h</c>: exactly 72 hours is still red, 72 hours and one
/// second is not, and an expiry equal to <c>now</c> is already expired. A bundle with no date is never red —
/// there is no cycle to count down and nothing to announce.
/// </para>
///
/// <para>
/// The dedup identity is <b>operator + source family + bundle key + expiry instant</b>. The source family
/// collapses <c>itad</c> and <c>itad_global</c> (same provider, same bundle) and leaves <c>ggdeals</c> alone
/// (its keys are a client-owned heuristic), and it is the same family <see cref="BundlePurchaseRules.HidesBundle"/>
/// uses to hide a purchased bundle from the pending catalog — so a purchase and a reminder can never disagree
/// about which cards are the same bundle. No title ever takes part: a title is not identity.
/// </para>
///
/// <para>
/// <see cref="BuildMessage"/> is the only place message text is created, and it sanitizes: control characters
/// (including the newlines that would forge extra ntfy headers) are removed, every field is truncated to its
/// column, and the click target must be an absolute HTTPS URL or it is dropped. The sender applies the same
/// HTTPS rule again, and the dispatcher stores only an outcome name on failure, so no URL, topic or token can
/// reach the database.
/// </para>
/// </summary>
public static class BundleAlertPolicy
{
    /// <summary>
    /// Policy generation folded into the dedup key. Bumping it intentionally re-alerts every currently red
    /// bundle exactly once; changing behaviour without bumping it does not.
    /// </summary>
    public const int TriggerVersion = 1;

    /// <summary>Last day that is red (spec S1). 3 days is red; 4 is not.</summary>
    public const int CriticalMaxDays = 3;

    /// <summary>The red window in hours: <see cref="CriticalMaxDays"/> days, inclusive at both ends of the rule.</summary>
    public const int AlertWindowHours = CriticalMaxDays * 24;

    /// <summary>Longest dedup key the outbox column accepts, shared with the offer outbox shape.</summary>
    public const int MaxDedupKeyLength = 320;

    /// <summary>Column limits of <c>bundle_alert_events</c>, applied to every generated string.</summary>
    public const int MaxTitleLength = 512;
    public const int MaxMessageLength = 1024;
    public const int MaxUrlLength = 1024;

    private const string ItadFamily = BundleCatalogOrder.ItadSource;
    private const string KeyPrefix = "bundle-alert";

    /// <summary>
    /// Red window test. <paramref name="expiresAtUtc"/> null means "the provider published no date", which is
    /// never red. The comparison is inclusive on the far edge and exclusive on <paramref name="nowUtc"/>.
    /// </summary>
    public static bool IsWithinAlertWindow(DateTime? expiresAtUtc, DateTime nowUtc)
    {
        if (expiresAtUtc is not { } expiresAt)
        {
            return false;
        }

        var expiry = ToUtc(expiresAt);
        var now = ToUtc(nowUtc);
        return expiry > now && expiry <= now.AddHours(AlertWindowHours);
    }

    /// <summary>
    /// Remaining whole days rounded up, exactly like the badge: 23 hours is 1 day, 72 hours is 3 days. Null when
    /// there is no date or the bundle already expired — there is no day left to announce.
    /// </summary>
    public static int? DaysRemaining(DateTime? expiresAtUtc, DateTime nowUtc)
    {
        if (expiresAtUtc is not { } expiresAt)
        {
            return null;
        }

        var delta = ToUtc(expiresAt) - ToUtc(nowUtc);
        if (delta <= TimeSpan.Zero)
        {
            return null;
        }

        return (int)Math.Ceiling(delta.TotalHours / 24d);
    }

    /// <summary>
    /// Dedup family of a provider source. <c>itad</c> and <c>itad_global</c> are the same provider reporting the
    /// same bundle, so they are one family; every other source (today <c>ggdeals</c>) is its own.
    /// <see cref="BundlePurchaseRules.HidesBundle"/> is the authority on that mapping, and the checks assert this
    /// helper agrees with it for every known source pair.
    /// </summary>
    public static string SourceFamily(string source) =>
        string.Equals(source, ItadFamily, StringComparison.Ordinal) ||
        string.Equals(source, BundleCatalogOrder.ItadGlobalSource, StringComparison.Ordinal)
            ? ItadFamily
            : source;

    /// <summary>
    /// The unique identity of one reminder cycle: operator + family + bundle key + expiry instant. Two reads of
    /// the same bundle with the same expiry produce the same key, so a job that runs every fifteen minutes
    /// inserts once; a moved expiry is a new cycle with a new key.
    ///
    /// The expiry is formatted to microseconds, the precision PostgreSQL keeps for <c>timestamptz</c>, so the
    /// instant the producer read and the instant the dispatcher re-reads render identically.
    /// </summary>
    public static string DedupKey(int userId, string source, string bundleKey, DateTime expiresAtUtc)
    {
        var expiry = ToUtc(expiresAtUtc).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"{KeyPrefix}:v{TriggerVersion}:{userId}:{SourceFamily(source)}:{bundleKey}:{expiry}");

        // The column is bounded and the longest legal bundle key fits (128 + the fixed parts). A longer value
        // would be silently truncated by the database and two different cycles could collide, so it is refused
        // here instead: a bundle key that big is corrupt data, not a bundle.
        return key.Length <= MaxDedupKeyLength
            ? key
            : throw new ArgumentException("El bundle key no cabe en la clave de deduplicación.", nameof(bundleKey));
    }

    /// <summary>
    /// The message of one reminder: the bundle name, the explicit ceil day count and the UTC expiry instant in
    /// the body, an HTTPS click target when the provider gave one. Every field is sanitized and truncated here so
    /// the stored row is always storable and always safe to hand to the ntfy client.
    /// </summary>
    public static BundleAlertMessage BuildMessage(BundleAlertCandidate bundle, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var title = SanitizeText(bundle.Title, MaxTitleLength);
        var days = DaysRemaining(bundle.ExpiresAt, nowUtc) ?? 1;
        var dayText = days == 1 ? "1 día" : $"{days} días";

        var body = new StringBuilder();
        body.Append('«').Append(title).Append("» vence en ").Append(dayText).Append(" (")
            .Append(ToUtc(bundle.ExpiresAt).ToString("yyyy-MM-dd HH:mm' UTC'", CultureInfo.InvariantCulture))
            .Append(')');
        var shop = SanitizeText(bundle.ShopName, 128);
        if (shop.Length > 0)
        {
            body.Append(". Tienda: ").Append(shop);
        }

        return new BundleAlertMessage(
            SanitizeText($"Bundle por vencer · {title}", MaxTitleLength),
            SanitizeText(body.ToString(), MaxMessageLength),
            SanitizeUrl(bundle.DealUrl) ?? SanitizeUrl(bundle.PageUrl));
    }

    /// <summary>
    /// Removes every control character (a newline in a title would forge an extra ntfy header), collapses the
    /// whitespace it leaves behind and truncates to <paramref name="maxLength"/>.
    /// </summary>
    public static string SanitizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The ntfy Click target: an absolute HTTPS URL with no control characters, or null. A provider URL of any
    /// other scheme is dropped rather than pushed, so a reminder can never become a redirect to something the
    /// provider chose.
    /// </summary>
    public static string? SanitizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxUrlLength)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Any(char.IsControl))
        {
            return null;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? trimmed
            : null;
    }

    /// <summary>
    /// Normalizes a provider timestamp. Npgsql returns <see cref="DateTimeKind.Utc"/> for <c>timestamptz</c>; the
    /// other two kinds are handled instead of assumed, because a value built by a test or a fixture arrives
    /// unspecified and <c>ToUniversalTime</c> on it would silently shift the instant by the server's offset.
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
