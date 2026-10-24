using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// A wished entry the person acquired and chose to drop from the wishlist locally (spec S3). One row per
/// <c>(user, store, store_game_id)</c>; the presence of the row <b>is</b> the suppression, so clearing it is
/// a delete and there is no boolean to keep in sync.
///
/// <para>
/// It is deliberately its own table and not a new <c>user_library.state</c>: the wishlist sync deletes the
/// rows Steam no longer returns and re-inserts the ones it does, so a "dismissed" state would have to be
/// recreated on every sync and would change what <c>user_library</c> means for every other reader.
/// <see cref="WishlistSyncService"/> reads this table before writing a row.
/// </para>
///
/// <para>
/// Steam is the only wishlist source today, and the removal stays local: nothing here is ever sent to
/// Steam, so the person must remove the game in Steam too (the dialog says so). The row survives merges
/// because it carries no <c>game_id</c>: the store id is the identity, exactly like <c>user_library</c>.
/// </para>
/// </summary>
[Table("wishlist_exclusions")]
public class WishlistExclusion : BaseModel
{
    [Key]
    [Column("wishlist_exclusion_id")]
    public long WishlistExclusionId { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    /// <summary>Store the suppressed wishlist row came from (<c>steam</c> today).</summary>
    [Required]
    [StringLength(32)]
    [Column("store")]
    public string Store { get; set; } = string.Empty;

    /// <summary>The suppressed row's own store id: the Steam appid for a Steam wishlist entry.</summary>
    [Required]
    [StringLength(64)]
    [Column("store_game_id")]
    public string StoreGameId { get; set; } = string.Empty;

    public User? User { get; set; }
}
