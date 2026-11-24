using System;
using System.Security.Claims;
using System.Threading.Tasks;
using LibraryHub.Models;
using LibraryHub.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.Controllers
{
    public class LibraryReviewController : Controller
    {
        private readonly DataContext _context;

        public LibraryReviewController(DataContext context)
        {
            _context = context;
        }

        [HttpPost]
        [Authorize(Roles = "Patron")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            LibraryReviewCreateViewModel model)
        {
            // Không cho phép Admin hoặc Librarian gửi đánh giá Patron.
            if (User.IsInRole("Admin") || User.IsInRole("Librarian"))
            {
                return Forbid();
            }

            if (!ModelState.IsValid)
            {
                TempData["ReviewError"] =
                    "Vui lòng kiểm tra lại số sao và nội dung đánh giá.";

                return RedirectToAction("Index", "Home");
            }

            // Lấy UserID từ tài khoản đang đăng nhập.
            var userIdClaim = User.FindFirstValue(
                ClaimTypes.NameIdentifier);

            if (!int.TryParse(userIdClaim, out int userId))
            {
                TempData["ReviewError"] =
                    "Không xác định được tài khoản đăng nhập.";

                return RedirectToAction("Index", "Home");
            }

            // Tìm hồ sơ Patron tương ứng với tài khoản.
            var patron = await _context.Patrons
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserID == userId);

            if (patron == null)
            {
                TempData["ReviewError"] =
                    "Tài khoản của bạn chưa có hồ sơ độc giả. Vui lòng liên hệ quản trị viên.";

                return RedirectToAction("Index", "Home");
            }

            // Tạo đánh giá mới.
            var review = new tblLibraryReview
            {
                PatronID = patron.PatronID,
                FullName = patron.PatronName,
                Avatar = patron.ImagePatron,
                ReviewerTitle = "Độc giả thư viện",
                Rating = model.Rating,
                ReviewContent = model.ReviewContent.Trim(),

                // Đánh giá phải được duyệt trước khi hiển thị.
                IsApproved = false,
                IsActive = true,

                CreateDate = DateTime.Now,
                UpdateDate = null
            };

            _context.LibraryReviews.Add(review);

            await _context.SaveChangesAsync();

            TempData["ReviewSuccess"] =
                "Cảm ơn bạn đã gửi đánh giá! Nội dung sẽ được hiển thị sau khi quản trị viên phê duyệt.";

            return RedirectToAction("Index", "Home");
        }
    }
}