using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// Which queried game a <see cref="ExternalBundle"/> was seen in. The relation carries its own
/// <see cref="Source"/> so a provider refresh purges only its own links without joining the bundle.
/// </summary>
[Table("external_bundle_games")]
public class ExternalBundleGame : BaseModel
{
    [Key]
    [Column("external_bundle_game_id")]
    public long ExternalBundleGameId { get; set; }

    [Column("external_bundle_id")]
    public long ExternalBundleId { get; set; }

    [Column("steam_game_id")]
    public int SteamGameId { get; set; }

    [Column("source")]
    [Required]
    [StringLength(16)]
    public string Source { get; set; } = string.Empty;

    [Column("observed_at", TypeName = "timestamp with time zone")]
    public DateTime ObservedAt { get; set; }

    public ExternalBundle? Bundle { get; set; }

    public SteamGame? SteamGame { get; set; }
}
