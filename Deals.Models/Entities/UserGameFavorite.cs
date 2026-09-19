using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Deals.Models.Models;

namespace Deals.Models.Entities;

/// <summary>
/// A game the user marked as favorite. Row present = favorite; unmarking deletes the row, so there is no
/// boolean to keep in sync. Deliberately its own table and NOT a column of <see cref="GameReview"/>: a
/// favorite belongs to the game, not to a playthrough, and a game can hold several reviews.
/// </summary>
[Table("user_game_favorites")]
public class UserGameFavorite : BaseModel
{
    [Key]
    [Column("user_id")]
    public int UserId { get; set; }

    [Key]
    [Column("game_id")]
    public long GameId { get; set; }
}
