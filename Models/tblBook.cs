using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace LibraryHub.Models
{
    [Table("tblBook")]
    public class tblBook
    {
        [Key]
        public int BookID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập tiêu đề sách.")]
        public string TitleBook { get; set; } = string.Empty;

        public string? Description { get; set; }

        [Required(ErrorMessage = "Vui lòng chọn nhà xuất bản.")]
        [NotMapped]
        public int? PublisherID { get; set; }

        [Required(ErrorMessage = "Vui lòng chọn tác giả.")]
        [NotMapped]
        public int? AuthorID { get; set; }

        // Lưu tên nhà xuất bản và tác giả như cấu trúc hiện tại
        [Required(ErrorMessage = "Vui lòng nhập nhà xuất bản.")]
        public string? Publisher { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập tác giả.")]
        public string? Author { get; set; }

        public bool IsActive { get; set; } = true;

        public int BookQuantity { get; set; }

        public int BorrowQuantity { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập năm xuất bản.")]
        public int? PublicationYear { get; set; }

        public int CategoryID { get; set; }

        [ForeignKey(nameof(CategoryID))]
        public tblCategory? category { get; set; }

        // Đường dẫn ảnh sách
        public string? ImageBook { get; set; }

        public int TotalReaders { get; set; }

        [JsonIgnore]
        public virtual ICollection<tblBorrowingDetail>? borrowingDetail { get; set; }

        public virtual ICollection<tblReturnDetail>? returnDetail { get; set; }
    }
}