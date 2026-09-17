using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Deals.Models.Entities;

/// <summary>
/// Daily FX rate snapshot used to derive approximate prices. One row per (base, quote, rate_date),
/// so no audit fields: the fetch is the observation. Composite key mapped in AppDbContext.
/// </summary>
[Table("fx_rates")]
public class FxRate
{
    [Column("base")]
    [Required]
    [StringLength(3)]
    public string Base { get; set; } = string.Empty;

    [Column("quote")]
    [Required]
    [StringLength(3)]
    public string Quote { get; set; } = string.Empty;

    [Column("rate", TypeName = "numeric(18,8)")]
    public decimal Rate { get; set; }

    [Column("rate_date", TypeName = "date")]
    public DateOnly RateDate { get; set; }

    [Column("source")]
    [Required]
    [StringLength(32)]
    public string Source { get; set; } = string.Empty;

    [Column("fetched_at", TypeName = "timestamp with time zone")]
    public DateTime FetchedAt { get; set; }
}
