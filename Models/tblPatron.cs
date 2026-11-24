using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace LibraryHub.Models
{
    [Table("tblPatron")]
    public class tblPatron
    {
        [Key]
        public int PatronID { get; set; }
        // Thêm cột này để liên kết với tblUsers
        public int? UserID { get; set; }

        [ForeignKey(nameof(UserID))]
        public virtual tblUsers? User { get; set; }

        public string? ImagePatron { get; set; }

        public string PatronName { get; set; }
        public string? Address { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public DateTime? RegisterDate { get; set; }
        public bool IsActive { get; set; }
        public virtual ICollection<tblBorrowing> borrowing { get; set; } = new List<tblBorrowing>();
    }
}