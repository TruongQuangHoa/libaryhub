
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LibraryHub.Models
{
    [Table("tblLibraryReview")]
    public class tblLibraryReview
    {
        [Key]
        public int ReviewID { get; set; }

        // Có thể không liên kết tài khoản độc giả
        public int? PatronID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập tên người đánh giá.")]
        [StringLength(150)]
        public string FullName { get; set; } = string.Empty;

        [StringLength(150)]
        public string? ReviewerTitle { get; set; }

        [StringLength(500)]
        public string? Avatar { get; set; }

        [Range(1, 5, ErrorMessage = "Điểm đánh giá phải từ 1 đến 5 sao.")]
        public int Rating { get; set; } = 5;

        [Required(ErrorMessage = "Vui lòng nhập nội dung đánh giá.")]
        public string ReviewContent { get; set; } = string.Empty;

        // Trạng thái kiểm duyệt
        public bool IsApproved { get; set; } = false;

        // Trạng thái hiển thị
        public bool IsActive { get; set; } = true;

        public DateTime CreateDate { get; set; } = DateTime.Now;

        public DateTime? UpdateDate { get; set; }

        // Quan hệ với độc giả
        [ForeignKey(nameof(PatronID))]
        public virtual tblPatron? Patron { get; set; }
    }
}
