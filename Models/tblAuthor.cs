
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LibraryHub.Models
{
    [Table("tblAuthor")]
    public class tblAuthor
    {
        [Key]
        public int AuthorID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập họ và tên tác giả.")]
        public string FullName { get; set; } = string.Empty;

        public DateTime? DateOfBirth { get; set; }

        public string? Gender { get; set; }

        public string? Nationality { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập lĩnh vực/chuyên môn.")]
        public string? Specialization { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập tiểu sử tác giả.")]
        public string? Biography { get; set; }

        public string? ImageAuthor { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập số đầu sách.")]
        public int BookCount { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập số lượt đọc.")]
        public int TotalReaders { get; set; }

        public decimal? Rating { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreateDate { get; set; } = DateTime.Now;

        public DateTime? UpdateDate { get; set; }

        // Khóa ngoại nhà xuất bản
        [Required(ErrorMessage = "Vui lòng chọn nhà xuất bản.")]
        public int? PublisherID { get; set; }

        [ForeignKey("PublisherID")]
        public tblPublisher? Publisher { get; set; }
    }
}
