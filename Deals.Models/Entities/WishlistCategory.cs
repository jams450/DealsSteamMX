using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

[Table("wishlist_categories")]
public class WishlistCategory : BaseModel
{
    [Key, Column("wishlist_category_id")]
    public long WishlistCategoryId { get; set; }
    [Column("user_id")] public int UserId { get; set; }
    [Required, StringLength(80), Column("name")] public string Name { get; set; } = string.Empty;
    [Required, StringLength(80), Column("normalized_name")] public string NormalizedName { get; set; } = string.Empty;
    public User? User { get; set; }
    public ICollection<WishlistCategoryItem> Items { get; set; } = new List<WishlistCategoryItem>();
}
