using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// One selected tier item of a <see cref="BundlePurchase"/> (spec S5): the membership the person bought,
/// its resolved canonical game and what the import did with it.
///
/// <para>
/// A duplicate keeps its row on purpose. "Already owned on this platform" is still a fact of the bundle
/// — the duplicate inventory is built from these rows — and it is what makes one duplicate per purchased
/// selected game, instead of a silent skip. <see cref="CanonicalSource"/> says how identity was decided:
/// the item's ITAD UUID (<c>itad</c>) or an explicit canonical link the person chose (<c>manual</c>).
/// </para>
///
/// <para>
/// <see cref="GameId"/> is a <c>NO ACTION</c> foreign key: a canonical merge must repoint it (the merge
/// service does) and must never silently drop acquisition evidence through a cascade.
/// </para>
/// </summary>
[Table("bundle_purchase_games")]
public class BundlePurchaseGame : BaseModel
{
    [Key]
    [Column("bundle_purchase_game_id")]
    public long BundlePurchaseGameId { get; set; }

    [Column("bundle_purchase_id")]
    public long BundlePurchaseId { get; set; }

    /// <summary>
    /// Stable key of the tier item: <c>itad:&lt;uuid&gt;</c> when the provider gave one, else a content
    /// hash. Unique per purchase, so a replay cannot record the same item twice.
    /// </summary>
    [Column("item_key")]
    [Required]
    [StringLength(128)]
    public string ItemKey { get; set; } = string.Empty;

    [Column("title")]
    [Required]
    [StringLength(256)]
    public string Title { get; set; } = string.Empty;

    [Column("item_type")]
    [StringLength(32)]
    public string? ItemType { get; set; }

    [Column("itad_game_id")]
    [StringLength(36)]
    public string? ItadGameId { get; set; }

    /// <summary>Canonical game this item was resolved to; never derived from a title.</summary>
    [Column("game_id")]
    public long GameId { get; set; }

    /// <summary><c>itad</c> (UUID namespace) or <c>manual</c> (explicit link), mirrored by a CHECK.</summary>
    [Column("canonical_source")]
    [Required]
    [StringLength(8)]
    public string CanonicalSource { get; set; } = string.Empty;

    /// <summary>Platform this game was imported into (tier-wide or per-game override).</summary>
    [Column("platform")]
    [Required]
    [StringLength(32)]
    public string Platform { get; set; } = string.Empty;

    /// <summary><c>imported</c> or <c>duplicate</c>, mirrored by a CHECK.</summary>
    [Column("outcome")]
    [Required]
    [StringLength(16)]
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The row this purchase created, or the owned row that already held the game.</summary>
    [Column("user_library_id")]
    public long? UserLibraryId { get; set; }

    /// <summary>True when this item's Steam wishlist row was removed (duplicates included).</summary>
    [Column("wishlist_removed")]
    public bool WishlistRemoved { get; set; }

    public BundlePurchase? Purchase { get; set; }

    public Game? Game { get; set; }
}
