using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace LibraryHub.Models
{
    [Table("tblLibrarian")]
    public class tblLibrarian
    {
        [Key]
        public int LibrarianID { get; set; }
        public string? FullName { get; set; }
        public DateTime? Birth { get; set; }
        public string? Gender { get; set; }
        public string? Address { get; set; }
        public string? StatusLibrarian { get; set; }
        public string? CCCD { get; set; }
        public string? Nation { get; set; }
        public string? Religion { get; set; }
        public string? NumberPhone { get; set; }
        public string? NumberBHXH { get; set; }
        public bool IsActive { get; set; }
        public string? Hamlet { get; set; }
        public string? Commune { get; set; }
        public string? Province { get; set; }
        public string? Nationality { get; set; }
        public string? Images { get; set; }

        [Required(ErrorMessage = "Email is required.")]
        [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
        public string? Email { get; set; }

        public int? UserID { get; set; }

        [ForeignKey(nameof(UserID))]
        public virtual tblUsers? User { get; set; }
        public virtual ICollection<tblBorrowing> borrowing { get; set; } = new List<tblBorrowing>();
        public virtual ICollection<tblReturn> _return { get; set; } = new List<tblReturn>();
    }
}