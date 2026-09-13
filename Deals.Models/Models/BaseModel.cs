using System.ComponentModel.DataAnnotations.Schema;

namespace Deals.Models.Models
{
    public class BaseModel
    {
        [Column("created_at")]
        public DateTime? Created { get; set; }

        [Column("updated_at")]
        public DateTime? Updated { get; set; }

        [Column("created_by")]
        public string? CreatedBy { get; set; }

        [Column("updated_by")]
        public string? UpdatedBy { get; set; }
    }
}
