using System.Security.Claims;
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.Components
{
    public class LibraryReviewViewComponent : ViewComponent
    {
        private readonly DataContext _context;

        public LibraryReviewViewComponent(DataContext context)
        {
            _context = context;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            var user = HttpContext.User;

            // Kiểm tra người dùng có phải Patron đang đăng nhập không.
            bool isPatron =
                user.Identity?.IsAuthenticated == true &&
                user.IsInRole("Patron") &&
                !user.IsInRole("Admin") &&
                !user.IsInRole("Librarian");

            ViewBag.IsPatron = false;
            ViewBag.IsAuthenticated =
                user.Identity?.IsAuthenticated == true;

            ViewBag.IsPatronProfileMissing = false;

            if (isPatron)
            {
                var userIdClaim = user.FindFirstValue(
                    ClaimTypes.NameIdentifier);

                if (int.TryParse(userIdClaim, out int userId))
                {
                    var patronExists = await _context.Patrons
                        .AsNoTracking()
                        .AnyAsync(p => p.UserID == userId);

                    ViewBag.IsPatron = patronExists;
                    ViewBag.IsPatronProfileMissing = !patronExists;
                }
            }

            // Chỉ lấy những đánh giá đã được duyệt và đang hoạt động.
            var reviews = await _context.LibraryReviews
                .AsNoTracking()
                .Where(r => r.IsApproved && r.IsActive)
                .OrderByDescending(r => r.CreateDate)
                .ToListAsync();

            return View(reviews);
        }
    }
}