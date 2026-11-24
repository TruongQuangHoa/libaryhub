using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace LibraryHub.Models
{
    [Table("tblBorrowingDetail")]
    public class tblBorrowingDetail
    {
        [Key]
        public int BorrowingDetailID { get; set; }
        public int BorrowingID { get; set; }
        public int BookID { get; set; }
        [Required(ErrorMessage = "Vui lòng nhập phí mượn sách.")]
        [Range(0, double.MaxValue, ErrorMessage = "Phí mượn sách phải lớn hơn hoặc bằng 0.")]
        [Column(TypeName = "decimal(18,2)")]
        public decimal BorrowingFee { get; set; }
        public bool IsActive { get; set; }

        [ForeignKey(nameof(BorrowingID))]
        public tblBorrowing? borrowing { get; set; }

        // Quan hệ ngược lại sách
        [JsonIgnore] // chặn vòng lặp
        [ForeignKey(nameof(BookID))]
        public virtual tblBook? book { get; set; }
    }
}