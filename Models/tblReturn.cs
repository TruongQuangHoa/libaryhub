using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace LibraryHub.Models
{
    [Table("tblReturn")]
    public class tblReturn
    {
        [Key]
        public int ReturnID { get; set; }
        public int BorrowingID { get; set; }
        [ForeignKey(nameof(BorrowingID))]
        public tblBorrowing? borrowing { get; set; }
        public DateTime? ReturnDate { get; set; }
        public int LibrarianID { get; set; }
        [ForeignKey(nameof(LibrarianID))]
        public tblLibrarian? librarian { get; set; }
        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalFine { get; set; }
        public bool IsActive { get; set; }
        public virtual ICollection<tblReturnDetail>? returnDetail { get; set; }
    }
}