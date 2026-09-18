using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// A durable external identifier of a <see cref="Game"/>. <c>(namespace, external_id)</c> is the real
/// identity: <c>namespace</c> reuses the <c>user_library.store</c> vocabulary plus provider namespaces
/// (<c>steam</c>, <c>itad</c>, ...), so a Playnite import seeds mappings without translation.
/// </summary>
[Table("game_external_ids")]
public class GameExternalId : BaseModel
{
    [Key]
    [Column("game_external_id")]
    public long GameExternalIdId { get; set; }

    [Column("game_id")]
    public long GameId { get; set; }

    [Column("namespace")]
    [Required]
    [StringLength(32)]
    public string NamespaceName { get; set; } = string.Empty;

    [Column("external_id")]
    [Required]
    [StringLength(64)]
    public string ExternalId { get; set; } = string.Empty;

    public Game? Game { get; set; }
}
