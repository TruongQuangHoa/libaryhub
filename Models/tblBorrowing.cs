using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace LibraryHub.Models
{
    [Table("tblBorrowing")]
    public class tblBorrowing
    {
        [Key]
        public int BorrowingID { get; set; }
        public int PatronID { get; set; }
        [ForeignKey(nameof(PatronID))]
        public tblPatron? patron { get; set; }
        public DateTime? BorrowDate { get; set; }
        public DateTime? DueDate { get; set; }
        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalBorrowingFee { get; set; }
        // Thêm property tính toán – KHÔNG lưu vào DB
        [NotMapped]
        public string BorrowingStatusDisplay
        {
            get
            {
                // 1. Đã trả (IsActive = false)
                if (!IsActive)
                    return "Đã trả";

                // 2. Quá hạn: so sánh thời gian hiện tại với DueDate (có giờ)
                if (DueDate.HasValue && DateTime.Now > DueDate.Value)
                    return "Quá hạn";

                // 3. Còn lại: đang mượn bình thường
                return "Đang mượn";
            }
        }
        public DateTime? CreateDate { get; set; }
        public DateTime? UpdateDate { get; set; }
        public int LibrarianID { get; set; }
        [ForeignKey(nameof(LibrarianID))]
        public tblLibrarian? librarian { get; set; }
        public bool IsActive { get; set; } = true;
        public virtual ICollection<tblBorrowingDetail>? borrowingDetail { get; set; }
    }
}