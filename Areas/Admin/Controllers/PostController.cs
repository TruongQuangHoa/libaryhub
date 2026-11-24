
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using LibraryHub.Models;
using LibraryHub.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Librarian")]
    public class PostController : Controller
    {
        private readonly DataContext _context;
        private readonly IWebHostEnvironment _environment;

        private const long MaxImageSize = 5 * 1024 * 1024;

        private static readonly string[] AllowedExtensions =
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp"
        };

        public PostController(
            DataContext context,
            IWebHostEnvironment environment)
        {
            _context = context;
            _environment = environment;
        }

        // =====================================
        // INDEX - DANH SÁCH, TÌM KIẾM, LỌC
        // =====================================
        public async Task<IActionResult> Index(
            string? keyword,
            string? category,
            string? status)
        {
            var query = _context.Posts
                .AsNoTracking()
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();

                query = query.Where(p =>
                    p.Title.Contains(keyword) ||
                    (p.AuthorName != null &&
                     p.AuthorName.Contains(keyword)) ||
                    (p.Summary != null &&
                     p.Summary.Contains(keyword)));
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                query = query.Where(p => p.Category == category);
            }

            var now = DateTime.Now;

            switch (status?.ToLowerInvariant())
            {
                case "active":
                    query = query.Where(p => p.IsActive);
                    break;

                case "inactive":
                    query = query.Where(p => !p.IsActive);
                    break;

                case "published":
                    query = query.Where(p =>
                        p.IsPublished &&
                        (!p.PublishedDate.HasValue ||
                         p.PublishedDate.Value <= now));
                    break;

                case "scheduled":
                    query = query.Where(p =>
                        p.IsPublished &&
                        p.PublishedDate.HasValue &&
                        p.PublishedDate.Value > now);
                    break;

                case "draft":
                    query = query.Where(p => !p.IsPublished);
                    break;

                case "featured":
                    query = query.Where(p => p.IsFeatured);
                    break;
            }

            var model = new PostManagementViewModel
            {
                Keyword = keyword,
                CategoryFilter = category,
                StatusFilter = status,

                Posts = await query
                    .OrderByDescending(p => p.CreatedDate)
                    .ToListAsync(),

                Categories = await _context.Posts
                    .Where(p => p.Category != null &&
                                p.Category != "")
                    .Select(p => p.Category!)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToListAsync()
            };

            return View(model);
        }

        // =====================================
        // CREATE - GET
        // =====================================
        [HttpGet]
        public IActionResult Create()
        {
            return View(new PostManagementViewModel
            {
                IsActive = true,
                IsFeatured = false,
                IsPublished = false,
                PublishedDate = DateTime.Now
            });
        }

        // =====================================
        // CREATE - POST
        // =====================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            PostManagementViewModel model)
        {
            ValidatePostContent(model);

            string? thumbnailPath = null;
            string? avatarPath = null;

            if (!ModelState.IsValid)
                return View(model);

            try
            {
                if (model.ThumbnailFile != null &&
                    model.ThumbnailFile.Length > 0)
                {
                    thumbnailPath = await SaveImageAsync(
                        model.ThumbnailFile, "posts");

                    if (thumbnailPath == null)
                        return View(model);
                }

                if (model.AuthorAvatarFile != null &&
                    model.AuthorAvatarFile.Length > 0)
                {
                    avatarPath = await SaveImageAsync(
                        model.AuthorAvatarFile, "authors");

                    if (avatarPath == null)
                    {
                        DeleteUploadedImage(thumbnailPath);
                        return View(model);
                    }
                }

                var post = new tblPost
                {
                    Title = model.Title.Trim(),
                    Slug = await GenerateUniqueSlugAsync(model.Title),
                    Summary = model.Summary?.Trim(),
                    Content = model.Content,

                    AuthorName = model.AuthorName?.Trim(),
                    Category = model.Category?.Trim(),

                    Thumbnail = thumbnailPath,
                    AuthorAvatar = avatarPath,

                    IsFeatured = model.IsFeatured,
                    IsPublished = model.IsPublished,
                    IsActive = model.IsActive,

                    ViewCount = 0,
                    CreatedDate = DateTime.Now,
                    PublishedDate = model.IsPublished
                        ? (model.PublishedDate ?? DateTime.Now)
                        : null,

                    MetaTitle = model.MetaTitle?.Trim(),
                    MetaDescription = model.MetaDescription?.Trim()
                };

                _context.Posts.Add(post);
                await _context.SaveChangesAsync();

                TempData["Success"] = "Thêm bài viết thành công!";
                return RedirectToAction(nameof(Index));
            }
            catch
            {
                DeleteUploadedImage(thumbnailPath);
                DeleteUploadedImage(avatarPath);

                ModelState.AddModelError(
                    "",
                    "Không thể lưu bài viết. Vui lòng thử lại.");

                return View(model);
            }
        }

        // =====================================
        // EDIT - GET
        // =====================================
        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null || id <= 0)
                return NotFound();

            var post = await _context.Posts
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PostID == id);

            if (post == null)
                return NotFound();

            var model = new PostManagementViewModel
            {
                PostID = post.PostID,
                Title = post.Title,
                Summary = post.Summary,
                Content = post.Content,
                AuthorName = post.AuthorName,
                Category = post.Category,

                CurrentThumbnail = post.Thumbnail,
                CurrentAuthorAvatar = post.AuthorAvatar,

                IsFeatured = post.IsFeatured,
                IsPublished = post.IsPublished,
                IsActive = post.IsActive,
                PublishedDate = post.PublishedDate,

                MetaTitle = post.MetaTitle,
                MetaDescription = post.MetaDescription
            };

            return View(model);
        }

        // =====================================
        // EDIT - POST
        // =====================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            PostManagementViewModel model)
        {
            var post = await _context.Posts
                .FirstOrDefaultAsync(p => p.PostID == model.PostID);

            if (post == null)
                return NotFound();

            ValidatePostContent(model);

            if (!ModelState.IsValid)
            {
                model.CurrentThumbnail = post.Thumbnail;
                model.CurrentAuthorAvatar = post.AuthorAvatar;
                return View(model);
            }

            string? newThumbnail = null;
            string? newAvatar = null;

            try
            {
                if (model.ThumbnailFile != null &&
                    model.ThumbnailFile.Length > 0)
                {
                    newThumbnail = await SaveImageAsync(
                        model.ThumbnailFile, "posts");

                    if (newThumbnail == null)
                        return ReturnEditView(model, post);
                }

                if (model.AuthorAvatarFile != null &&
                    model.AuthorAvatarFile.Length > 0)
                {
                    newAvatar = await SaveImageAsync(
                        model.AuthorAvatarFile, "authors");

                    if (newAvatar == null)
                    {
                        DeleteUploadedImage(newThumbnail);
                        return ReturnEditView(model, post);
                    }
                }

                // Chỉ tạo lại Slug khi tiêu đề thay đổi
                if (!string.Equals(
                    post.Title,
                    model.Title.Trim(),
                    StringComparison.Ordinal))
                {
                    post.Slug = await GenerateUniqueSlugAsync(
                        model.Title, post.PostID);
                }

                post.Title = model.Title.Trim();
                post.Summary = model.Summary?.Trim();
                post.Content = model.Content;

                post.AuthorName = model.AuthorName?.Trim();
                post.Category = model.Category?.Trim();

                post.IsFeatured = model.IsFeatured;
                post.IsPublished = model.IsPublished;
                post.IsActive = model.IsActive;

                post.PublishedDate = model.IsPublished
                    ? (model.PublishedDate ?? DateTime.Now)
                    : null;

                post.MetaTitle = model.MetaTitle?.Trim();
                post.MetaDescription = model.MetaDescription?.Trim();

                post.UpdatedDate = DateTime.Now;

                string? oldThumbnail = post.Thumbnail;
                string? oldAvatar = post.AuthorAvatar;

                if (newThumbnail != null)
                    post.Thumbnail = newThumbnail;

                if (newAvatar != null)
                    post.AuthorAvatar = newAvatar;

                await _context.SaveChangesAsync();

                // Chỉ xóa ảnh cũ sau khi DB lưu thành công
                if (newThumbnail != null)
                    DeleteUploadedImage(oldThumbnail);

                if (newAvatar != null)
                    DeleteUploadedImage(oldAvatar);

                TempData["Success"] = "Cập nhật bài viết thành công!";
                return RedirectToAction(nameof(Index));
            }
            catch
            {
                DeleteUploadedImage(newThumbnail);
                DeleteUploadedImage(newAvatar);

                ModelState.AddModelError(
                    "",
                    "Không thể cập nhật bài viết. Vui lòng thử lại.");

                return ReturnEditView(model, post);
            }
        }

        // =====================================
        // DELETE - GET
        // =====================================
        [HttpGet]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null || id <= 0)
                return NotFound();

            var post = await _context.Posts
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PostID == id);

            if (post == null)
                return NotFound();

            return View(post);
        }

        // =====================================
        // DELETE - POST
        // =====================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var post = await _context.Posts
                .FirstOrDefaultAsync(p => p.PostID == id);

            if (post == null)
                return NotFound();

            var thumbnail = post.Thumbnail;
            var avatar = post.AuthorAvatar;

            _context.Posts.Remove(post);
            await _context.SaveChangesAsync();

            DeleteUploadedImage(thumbnail);
            DeleteUploadedImage(avatar);

            TempData["Success"] = "Xóa bài viết thành công!";
            return RedirectToAction(nameof(Index));
        }

        // =====================================
        // TOGGLE ACTIVE
        // =====================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var post = await _context.Posts.FindAsync(id);

            if (post == null)
                return NotFound();

            post.IsActive = !post.IsActive;
            post.UpdatedDate = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã cập nhật trạng thái bài viết.";
            return RedirectToAction(nameof(Index));
        }

        // =====================================
        // TOGGLE FEATURED
        // =====================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleFeatured(int id)
        {
            var post = await _context.Posts.FindAsync(id);

            if (post == null)
                return NotFound();

            post.IsFeatured = !post.IsFeatured;
            post.UpdatedDate = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã cập nhật bài viết nổi bật.";
            return RedirectToAction(nameof(Index));
        }

        // =====================================
        // TOGGLE PUBLISH
        // =====================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TogglePublish(int id)
        {
            var post = await _context.Posts.FindAsync(id);

            if (post == null)
                return NotFound();

            post.IsPublished = !post.IsPublished;

            if (post.IsPublished && !post.PublishedDate.HasValue)
                post.PublishedDate = DateTime.Now;

            if (!post.IsPublished)
                post.PublishedDate = null;

            post.UpdatedDate = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã cập nhật trạng thái xuất bản.";
            return RedirectToAction(nameof(Index));
        }

        // =====================================
        // VALIDATE CONTENT
        // =====================================
        private void ValidatePostContent(PostManagementViewModel model)
        {
            if (string.IsNullOrWhiteSpace(model.Content) ||
                model.Content.Trim() == "<p><br></p>")
            {
                ModelState.AddModelError(
                    nameof(model.Content),
                    "Vui lòng nhập nội dung bài viết.");
            }

            if (model.IsPublished &&
                model.PublishedDate == null)
            {
                ModelState.AddModelError(
                    nameof(model.PublishedDate),
                    "Vui lòng chọn ngày xuất bản.");
            }
        }

        // =====================================
        // SLUG
        // =====================================
        private async Task<string> GenerateUniqueSlugAsync(
            string title,
            int? excludePostId = null)
        {
            string baseSlug = CreateSlug(title);

            if (string.IsNullOrWhiteSpace(baseSlug))
                baseSlug = "bai-viet";

            string slug = baseSlug;
            int count = 1;

            while (await _context.Posts.AnyAsync(p =>
                p.Slug == slug &&
                (!excludePostId.HasValue ||
                 p.PostID != excludePostId.Value)))
            {
                count++;
                slug = $"{baseSlug}-{count}";
            }

            return slug;
        }

        private string CreateSlug(string text)
        {
            text = text.Trim().ToLowerInvariant()
                .Replace("đ", "d");

            string normalized = text.Normalize(
                NormalizationForm.FormD);

            var builder = new StringBuilder();

            foreach (char c in normalized)
            {
                var unicodeCategory =
                    System.Globalization.CharUnicodeInfo
                        .GetUnicodeCategory(c);

                if (unicodeCategory !=
                    System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    if (char.IsLetterOrDigit(c))
                        builder.Append(c);
                    else if (builder.Length > 0 &&
                             builder[builder.Length - 1] != '-')
                        builder.Append('-');
                }
            }

            return builder.ToString()
                .Normalize(NormalizationForm.FormC)
                .Trim('-');
        }

        // =====================================
        // SAVE IMAGE
        // =====================================
        private async Task<string?> SaveImageAsync(
            IFormFile imageFile,
            string folderName)
        {
            var extension = Path
                .GetExtension(imageFile.FileName)
                .ToLowerInvariant();

            if (!AllowedExtensions.Contains(extension))
            {
                ModelState.AddModelError(
                    "",
                    "Ảnh chỉ được sử dụng JPG, JPEG, PNG, GIF hoặc WEBP.");

                return null;
            }

            if (imageFile.Length > MaxImageSize)
            {
                ModelState.AddModelError(
                    "",
                    "Dung lượng ảnh không được vượt quá 5MB.");

                return null;
            }

            if (imageFile.Length == 0 ||
                string.IsNullOrWhiteSpace(imageFile.ContentType) ||
                !imageFile.ContentType.StartsWith(
                    "image/",
                    StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(
                    "",
                    "File tải lên không hợp lệ.");

                return null;
            }

            var uploadFolder = Path.Combine(
                _environment.WebRootPath,
                "uploads",
                folderName);

            Directory.CreateDirectory(uploadFolder);

            var fileName = $"{Guid.NewGuid():N}{extension}";
            var filePath = Path.Combine(uploadFolder, fileName);

            await using (var stream = new FileStream(
                filePath, FileMode.Create))
            {
                await imageFile.CopyToAsync(stream);
            }

            return $"/uploads/{folderName}/{fileName}";
        }

        // =====================================
        // DELETE UPLOADED IMAGE
        // =====================================
        private void DeleteUploadedImage(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                return;

            try
            {
                // Chỉ xóa ảnh trong đúng thư mục upload của bài viết
                var normalized = imagePath.Replace('\\', '/');

                if (!normalized.StartsWith(
                    "/uploads/posts/",
                    StringComparison.OrdinalIgnoreCase) &&
                    !normalized.StartsWith(
                    "/uploads/authors/",
                    StringComparison.OrdinalIgnoreCase))
                    return;

                var folder = normalized.StartsWith(
                    "/uploads/posts/",
                    StringComparison.OrdinalIgnoreCase)
                    ? "posts"
                    : "authors";

                var fileName = Path.GetFileName(normalized);

                if (string.IsNullOrWhiteSpace(fileName))
                    return;

                var filePath = Path.Combine(
                    _environment.WebRootPath,
                    "uploads",
                    folder,
                    fileName);

                if (System.IO.File.Exists(filePath))
                    System.IO.File.Delete(filePath);
            }
            catch
            {
                // Không làm hỏng thao tác dữ liệu nếu xóa file thất bại
            }
        }

        // =====================================
        // RETURN EDIT VIEW WITH CURRENT IMAGES
        // =====================================
        private IActionResult ReturnEditView(
            PostManagementViewModel model,
            tblPost post)
        {
            model.CurrentThumbnail = post.Thumbnail;
            model.CurrentAuthorAvatar = post.AuthorAvatar;

            return View("Edit", model);
        }
    }
}
