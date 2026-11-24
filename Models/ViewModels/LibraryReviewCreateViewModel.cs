using System.ComponentModel.DataAnnotations;

namespace LibraryHub.ViewModels
{
    public class LibraryReviewCreateViewModel
    {
        [Range(1, 5, ErrorMessage = "Vui lòng chọn số sao đánh giá.")]
        public int Rating { get; set; } = 5;

        [Required(ErrorMessage = "Vui lòng nhập nội dung đánh giá.")]
        [StringLength(5000, ErrorMessage = "Nội dung đánh giá không được vượt quá 5000 ký tự.")]
        public string ReviewContent { get; set; } = string.Empty;
    }
}