using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// One entry of a user's store library/wishlist snapshot, keyed by (user, store, store_game_id, state).
/// <see cref="State"/> tells whether the entry came from the wishlist or the owned library.
/// </summary>
[Table("user_library")]
public class UserLibrary : BaseModel
{
    [Key]
    [Column("user_library_id")]
    public long UserLibraryId { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    [Column("itad_game_id")]
    [StringLength(36)]
    public string? ItadGameId { get; set; }

    /// <summary>Nullable canonical link: <c>NULL</c> means "no canonical identity", a valid state.</summary>
    [Column("game_id")]
    public long? GameId { get; set; }

    [Column("store")]
    [Required]
    [StringLength(32)]
    public string Store { get; set; } = string.Empty;

    [Column("store_game_id")]
    [Required]
    [StringLength(64)]
    public string StoreGameId { get; set; } = string.Empty;

    [Column("title")]
    [Required]
    [StringLength(256)]
    public string Title { get; set; } = string.Empty;

    [Column("state")]
    [Required]
    [StringLength(16)]
    public string State { get; set; } = string.Empty;

    [Column("is_installed")]
    public bool? IsInstalled { get; set; }

    [Column("priority")]
    public int? Priority { get; set; }

    [Column("added_at", TypeName = "timestamp with time zone")]
    public DateTime? AddedAt { get; set; }

    [Column("imported_at", TypeName = "timestamp with time zone")]
    public DateTime ImportedAt { get; set; }

    public User? User { get; set; }

    public Game? Game { get; set; }
}
