
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LibraryHub.Models
{
    [Table("tblPost")]
    public class tblPost
    {
        [Key]
        public int PostID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập tiêu đề bài viết.")]
        [StringLength(250)]
        public string Title { get; set; } = string.Empty;

        [Required]
        [StringLength(300)]
        public string Slug { get; set; } = string.Empty;

        [StringLength(1000)]
        public string? Summary { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập nội dung bài viết.")]
        [Column(TypeName = "nvarchar(max)")]
        public string Content { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Thumbnail { get; set; }

        [StringLength(150)]
        public string? AuthorName { get; set; }

        [StringLength(500)]
        public string? AuthorAvatar { get; set; }

        [StringLength(150)]
        public string? Category { get; set; }

        public int ViewCount { get; set; } = 0;

        public bool IsFeatured { get; set; } = false;

        public bool IsPublished { get; set; } = false;

        public bool IsActive { get; set; } = true;

        public DateTime CreatedDate { get; set; } = DateTime.Now;

        public DateTime? PublishedDate { get; set; }

        public DateTime? UpdatedDate { get; set; }

        [StringLength(250)]
        public string? MetaTitle { get; set; }

        [StringLength(500)]
        public string? MetaDescription { get; set; }
    }
}
