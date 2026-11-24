
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Librarian")]
    public class LibraryReviewController : Controller
    {
        private readonly DataContext _context;
        private readonly IWebHostEnvironment _environment;

        public LibraryReviewController(
            DataContext context,
            IWebHostEnvironment environment)
        {
            _context = context;
            _environment = environment;
        }

        // =========================================================
        // INDEX - DANH SÁCH ĐÁNH GIÁ
        // =========================================================
        public async Task<IActionResult> Index()
        {
            var reviews = await _context.LibraryReviews
                .AsNoTracking()
                .OrderByDescending(r => r.CreateDate)
                .ThenByDescending(r => r.ReviewID)
                .ToListAsync();

            return View(reviews);
        }

        // =========================================================
        // CREATE - GET
        // =========================================================
        [HttpGet]
        public IActionResult Create()
        {
            var review = new tblLibraryReview
            {
                Rating = 5,
                IsApproved = false,
                IsActive = true,
                CreateDate = DateTime.Now
            };

            return View(review);
        }

        // =========================================================
        // CREATE - POST
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            tblLibraryReview model,
            IFormFile? imageFile)
        {
            // Không cho phép người dùng tự truyền mã đánh giá.
            model.ReviewID = 0;

            // Kiểm tra lại số sao ở phía server.
            if (model.Rating < 1 || model.Rating > 5)
            {
                ModelState.AddModelError(
                    nameof(model.Rating),
                    "Điểm đánh giá phải từ 1 đến 5 sao.");
            }

            if (!ModelState.IsValid)
                return View(model);

            model.FullName = model.FullName.Trim();
            model.ReviewContent = model.ReviewContent.Trim();

            if (!string.IsNullOrWhiteSpace(model.ReviewerTitle))
                model.ReviewerTitle = model.ReviewerTitle.Trim();

            model.CreateDate = DateTime.Now;
            model.UpdateDate = null;

            // Đánh giá mới cần được kiểm duyệt.
            model.IsApproved = false;

            if (imageFile != null && imageFile.Length > 0)
            {
                var imagePath = await SaveReviewImageAsync(imageFile);

                if (imagePath == null)
                    return View(model);

                model.Avatar = imagePath;
            }
            else
            {
                model.Avatar = null;
            }

            _context.LibraryReviews.Add(model);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Thêm đánh giá thư viện thành công. Đánh giá đang chờ duyệt.";

            return RedirectToAction(nameof(Index));
        }

        // =========================================================
        // EDIT - GET
        // =========================================================
        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null || id <= 0)
                return NotFound();

            var review = await _context.LibraryReviews
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.ReviewID == id.Value);

            if (review == null)
                return NotFound();

            return View(review);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            tblLibraryReview model,
            IFormFile? imageFile)
        {
            var existingReview = await _context.LibraryReviews
                .FirstOrDefaultAsync(r => r.ReviewID == model.ReviewID);

            if (existingReview == null)
                return NotFound();

            if (model.Rating < 1 || model.Rating > 5)
            {
                ModelState.AddModelError(
                    nameof(model.Rating),
                    "Điểm đánh giá phải từ 1 đến 5 sao.");
            }

            if (!ModelState.IsValid)
            {
                model.Avatar = existingReview.Avatar;
                return View(model);
            }

            string? oldAvatar = existingReview.Avatar;
            string? newImagePath = null;

            if (imageFile != null && imageFile.Length > 0)
            {
                newImagePath = await SaveReviewImageAsync(imageFile);

                if (newImagePath == null)
                {
                    model.Avatar = existingReview.Avatar;
                    return View(model);
                }
            }

            existingReview.FullName = model.FullName.Trim();

            existingReview.ReviewerTitle =
                string.IsNullOrWhiteSpace(model.ReviewerTitle)
                    ? null
                    : model.ReviewerTitle.Trim();

            existingReview.Rating = model.Rating;
            existingReview.ReviewContent = model.ReviewContent.Trim();
            existingReview.IsActive = model.IsActive;

            // Giữ nguyên PatronID, IsApproved và CreateDate.
            if (newImagePath != null)
                existingReview.Avatar = newImagePath;

            existingReview.UpdateDate = DateTime.Now;

            await _context.SaveChangesAsync();

            // Chỉ xóa ảnh cũ sau khi cập nhật DB thành công.
            if (newImagePath != null &&
                !string.IsNullOrWhiteSpace(oldAvatar))
            {
                DeleteReviewImage(oldAvatar);
            }

            TempData["Success"] = "Cập nhật đánh giá thành công.";

            return RedirectToAction(nameof(Index));
        }


        // =========================================================
        // DELETE - GET
        // =========================================================
        [HttpGet]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null || id <= 0)
                return NotFound();

            var review = await _context.LibraryReviews
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.ReviewID == id.Value);

            if (review == null)
                return NotFound();

            return View(review);
        }

        // =========================================================
        // DELETE - POST
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var review = await _context.LibraryReviews
                .FirstOrDefaultAsync(r => r.ReviewID == id);

            if (review == null)
                return NotFound();

            string? oldAvatar = review.Avatar;

            _context.LibraryReviews.Remove(review);

            await _context.SaveChangesAsync();

            if (!string.IsNullOrWhiteSpace(oldAvatar))
                DeleteReviewImage(oldAvatar);

            TempData["Success"] = "Xóa đánh giá thành công.";

            return RedirectToAction(nameof(Index));
        }

        // =========================================================
        // TOGGLE APPROVAL - DUYỆT / HỦY DUYỆT
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleApproval(int id)
        {
            var review = await _context.LibraryReviews
                .FirstOrDefaultAsync(r => r.ReviewID == id);

            if (review == null)
                return NotFound();

            review.IsApproved = !review.IsApproved;
            review.UpdateDate = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = review.IsApproved
                ? "Đã duyệt đánh giá thành công."
                : "Đã hủy duyệt đánh giá.";

            return RedirectToAction(nameof(Index));
        }

        // =========================================================
        // TOGGLE STATUS - BẬT / TẮT HIỂN THỊ
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var review = await _context.LibraryReviews
                .FirstOrDefaultAsync(r => r.ReviewID == id);

            if (review == null)
                return NotFound();

            review.IsActive = !review.IsActive;
            review.UpdateDate = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = review.IsActive
                ? "Đã bật hiển thị đánh giá."
                : "Đã ẩn đánh giá khỏi website.";

            return RedirectToAction(nameof(Index));
        }

        // =========================================================
        // SAVE REVIEW IMAGE
        // =========================================================
        private async Task<string?> SaveReviewImageAsync(IFormFile imageFile)
        {
            string[] allowedExtensions =
            {
                ".jpg",
                ".jpeg",
                ".png",
                ".gif",
                ".webp"
            };

            const long maxFileSize = 5 * 1024 * 1024;

            string extension = Path.GetExtension(imageFile.FileName)
                .ToLowerInvariant();

            if (!allowedExtensions.Contains(extension))
            {
                ModelState.AddModelError(
                    nameof(tblLibraryReview.Avatar),
                    "Ảnh phải có định dạng JPG, JPEG, PNG, GIF hoặc WEBP.");

                return null;
            }

            if (imageFile.Length > maxFileSize)
            {
                ModelState.AddModelError(
                    nameof(tblLibraryReview.Avatar),
                    "Dung lượng ảnh không được vượt quá 5MB.");

                return null;
            }

            if (string.IsNullOrWhiteSpace(imageFile.ContentType) ||
                !imageFile.ContentType.StartsWith(
                    "image/",
                    StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(
                    nameof(tblLibraryReview.Avatar),
                    "File được chọn không phải là hình ảnh.");

                return null;
            }

            string uploadFolder = Path.Combine(
                _environment.WebRootPath,
                "uploads",
                "library-reviews");

            Directory.CreateDirectory(uploadFolder);

            string fileName = $"{Guid.NewGuid():N}{extension}";
            string filePath = Path.Combine(uploadFolder, fileName);

            await using (var stream = new FileStream(
                filePath,
                FileMode.Create))
            {
                await imageFile.CopyToAsync(stream);
            }

            return $"/uploads/library-reviews/{fileName}";
        }

        // =========================================================
        // DELETE REVIEW IMAGE
        // =========================================================
        private void DeleteReviewImage(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                return;

            try
            {
                string fileName = Path.GetFileName(imagePath);

                if (string.IsNullOrWhiteSpace(fileName))
                    return;

                string filePath = Path.Combine(
                    _environment.WebRootPath,
                    "uploads",
                    "library-reviews",
                    fileName);

                if (System.IO.File.Exists(filePath))
                    System.IO.File.Delete(filePath);
            }
            catch
            {
                // Không để lỗi xóa ảnh ảnh hưởng đến dữ liệu database.
            }
        }
    }
}
