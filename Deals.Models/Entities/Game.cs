using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// Canonical game identity, shared by the comparator, library and (future) reviews. It is deliberately
/// decoupled from <see cref="SteamGame"/>: a game may exist without a Steam app id (Epic/GOG/Amazon/Xbox
/// exclusives). <see cref="NormalizedTitle"/> is a comparison/display key only, never an identity.
/// </summary>
[Table("games")]
public class Game : BaseModel
{
    [Key]
    [Column("game_id")]
    public long GameId { get; set; }

    [Column("title")]
    [Required]
    [StringLength(512)]
    public string Title { get; set; } = string.Empty;

    [Column("normalized_title")]
    [Required]
    [StringLength(512)]
    public string NormalizedTitle { get; set; } = string.Empty;

    [Column("type")]
    [StringLength(32)]
    public string? Type { get; set; }

    [Column("image_url")]
    [StringLength(512)]
    public string? ImageUrl { get; set; }

    [Column("is_free")]
    public bool IsFree { get; set; }

    [Column("release_year")]
    public int? ReleaseYear { get; set; }

    [Column("first_release_date", TypeName = "date")]
    public DateTime? FirstReleaseDate { get; set; }

    public ICollection<GameExternalId> ExternalIds { get; set; } = new List<GameExternalId>();
}
