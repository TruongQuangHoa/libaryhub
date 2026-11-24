using System;

namespace LibraryHub.Utilities
{
    public static class Functions
    {
        // Các biến tĩnh dùng chung để lưu thông tin người dùng hiện tại
        // (dùng để hiển thị header, kiểm tra đăng nhập, v.v.)
        public static int _UserID = 0;
        public static string _UserName = string.Empty;
        public static string _Email = string.Empty;
        public static string _RoleId = string.Empty;
        public static string _FullName = string.Empty;
        public static string _Message = string.Empty;
        public static string _Images = string.Empty;

        /// <summary>
        /// Tạo slug cho URL bài viết (ví dụ: news-tieu-de-bai-viet-123.html)
        /// </summary>
        public static string TitleSlugGeneration(string type, string? title, long id)
        {
            return type + "-" + SlugGenerator.SlugGenerator.GenerateSlug(title) + "-" + id.ToString() + ".html";
        }

        /// <summary>
        /// Lấy ngày giờ hiện tại theo định dạng dùng trong CSDL (yyyy-MM-dd HH:mm:ss)
        /// </summary>
        public static string GetCurrentDate()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        /// <summary>
        /// Kiểm tra xem người dùng đã đăng nhập chưa
        /// </summary>
        public static bool IsLogin()
        {
            return _UserID > 0 && 
                   !string.IsNullOrEmpty(_UserName) && 
                   !string.IsNullOrEmpty(_Email);
        }

    }
}