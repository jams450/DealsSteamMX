using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

[Table("wishlist_category_items")]
public class WishlistCategoryItem : BaseModel
{
    [Column("wishlist_category_id")] public long WishlistCategoryId { get; set; }
    [Column("user_id")] public int UserId { get; set; }
    [Column("user_library_id")] public long UserLibraryId { get; set; }
    public WishlistCategory? Category { get; set; }
    public UserLibrary? UserLibrary { get; set; }
}
