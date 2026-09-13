using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities
{
    [Table("user_sessions")]
    public class UserSession : BaseModel
    {
        [Key]
        [Column("session_id")]
        public Guid SessionId { get; set; } = Guid.NewGuid();

        [Column("user_id")]
        [Required]
        public int UserId { get; set; }

        [Column("refresh_token_hash")]
        [Required]
        [StringLength(128)]
        public string RefreshTokenHash { get; set; } = string.Empty;

        [Column("expires_at", TypeName = "timestamp with time zone")]
        [Required]
        public DateTime ExpiresAt { get; set; }

        [Column("revoked_at", TypeName = "timestamp with time zone")]
        public DateTime? RevokedAt { get; set; }

        [Column("replaced_by_session_id")]
        public Guid? ReplacedBySessionId { get; set; }

        [Column("ip")]
        [StringLength(64)]
        public string? Ip { get; set; }

        [Column("user_agent")]
        [StringLength(512)]
        public string? UserAgent { get; set; }

        [ForeignKey("UserId")]
        public virtual User User { get; set; } = null!;
    }
}
