using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities
{
    [Table("users")]
    public class User : BaseModel
    {
        [Key]
        [Column("user_id")]
        public int UserId { get; set; }

        [Column("name")]
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [Column("email")]
        [Required]
        [StringLength(100)]
        public string Email { get; set; } = string.Empty;

        [Column("password")]
        [Required]
        [StringLength(255)]
        public string Password { get; set; } = string.Empty;

        [Column("active")]
        public bool Active { get; set; } = true;

        [Column("admin")]
        public bool Admin { get; set; } = false;

        [Column("session_version")]
        public int SessionVersion { get; set; } = 1;

        [Column("failed_login_count")]
        public int FailedLoginCount { get; set; } = 0;

        [Column("locked_until", TypeName = "timestamp with time zone")]
        public DateTime? LockedUntil { get; set; }

        [Column("steam_id64")]
        [StringLength(20)]
        public string? SteamId64 { get; set; }

        [Column("wishlist_synced_at", TypeName = "timestamp with time zone")]
        public DateTime? WishlistSyncedAt { get; set; }

        /// <summary>
        /// Outcome of the last wishlist sync: "ok" | "inaccessible". Never-synced is derived from
        /// <see cref="WishlistSyncedAt"/> being null, so it is not stored here.
        /// </summary>
        [Column("wishlist_state")]
        [StringLength(16)]
        public string? WishlistState { get; set; }

        /// <summary>
        /// Minimum discount (0..95) a wishlist deal must show to count as viable for this user.
        /// </summary>
        [Column("min_viable_discount_percent")]
        public int MinViableDiscountPercent { get; set; } = 50;

        public virtual ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();
    }
}
