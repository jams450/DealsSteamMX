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

    /// <summary>
    /// Store where the game was acquired, which is <b>not</b> <see cref="Store"/>: the row lives in the
    /// library of the chosen platform while this says where the copy came from (a Steam game gifted from
    /// Epic, a console game bought in a shop). <c>NULL</c> means "not registered", which is every imported
    /// row: the wishlist → library acquisition is the only writer.
    /// </summary>
    [Column("acquired_store")]
    [StringLength(32)]
    public string? AcquiredStore { get; set; }

    /// <summary>
    /// How the game was acquired (<c>purchase | gift | free | bundle | subscription</c>), mirrored by a
    /// CHECK in <c>SQL/schema.sql</c>. <c>NULL</c> for imported rows and for its sibling
    /// <see cref="AcquiredStore"/>: the two are written together or not at all.
    /// </summary>
    [Column("acquisition_type")]
    [StringLength(16)]
    public string? AcquisitionType { get; set; }

    /// <summary>
    /// Classification of <see cref="AcquiredStore"/> (<c>official | keyshop | unknown</c>), mirrored by a
    /// CHECK in <c>SQL/schema.sql</c>. <c>NULL</c> means "not classified": every row written before this
    /// column existed and every client that does not send the field. It is optional on its own — an
    /// acquisition can record its shop without classifying it — and it is never inferred from a price or a
    /// provider, only from what the person selected.
    /// </summary>
    [Column("acquired_store_type")]
    [StringLength(16)]
    public string? AcquiredStoreType { get; set; }

    /// <summary>
    /// Where the offer behind this acquisition was discovered (<c>itad | ggdeals | direct | other</c>),
    /// mirrored by a CHECK. Optional like <see cref="AcquiredStoreType"/> and never derived from the prices
    /// the product already stores.
    /// </summary>
    [Column("acquisition_provider")]
    [StringLength(16)]
    public string? AcquisitionProvider { get; set; }

    /// <summary>
    /// Date-only acquisition date. Optional by contract: the feature never invents a date for a game the
    /// person does not remember buying, so <c>NULL</c> is a valid answer here.
    /// </summary>
    [Column("acquisition_date", TypeName = "date")]
    public DateOnly? AcquisitionDate { get; set; }

    public User? User { get; set; }

    public Game? Game { get; set; }
}
