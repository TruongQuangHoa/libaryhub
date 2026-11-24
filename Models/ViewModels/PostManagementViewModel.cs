
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using LibraryHub.Models;

namespace LibraryHub.ViewModels
{
    public class PostManagementViewModel
    {
        // Bộ lọc danh sách
        public string? Keyword { get; set; }
        public string? CategoryFilter { get; set; }
        public string? StatusFilter { get; set; }

        public List<tblPost> Posts { get; set; } = new();
        public List<string> Categories { get; set; } = new();

        // Thông tin bài viết
        public int PostID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập tiêu đề bài viết.")]
        [StringLength(250, ErrorMessage = "Tiêu đề không được vượt quá 250 ký tự.")]
        public string Title { get; set; } = string.Empty;

        [StringLength(1000, ErrorMessage = "Mô tả ngắn không được vượt quá 1000 ký tự.")]
        public string? Summary { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập nội dung bài viết.")]
        public string Content { get; set; } = string.Empty;

        [StringLength(150)]
        public string? AuthorName { get; set; }

        [StringLength(150)]
        public string? Category { get; set; }

        // Ảnh tải lên
        public IFormFile? ThumbnailFile { get; set; }
        public IFormFile? AuthorAvatarFile { get; set; }

        // Đường dẫn ảnh hiện tại khi chỉnh sửa
        public string? CurrentThumbnail { get; set; }
        public string? CurrentAuthorAvatar { get; set; }

        // Trạng thái
        public bool IsFeatured { get; set; }
        public bool IsPublished { get; set; }
        public bool IsActive { get; set; } = true;

        public System.DateTime? PublishedDate { get; set; }

        // SEO
        [StringLength(250)]
        public string? MetaTitle { get; set; }

        [StringLength(500)]
        public string? MetaDescription { get; set; }
    }
}
