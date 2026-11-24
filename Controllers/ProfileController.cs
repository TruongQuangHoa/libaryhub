using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Claims; // QUAN TRỌNG: Để dùng FindFirstValue và ClaimTypes
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore; // QUAN TRỌNG: Để dùng Include, FirstOrDefaultAsync, ToListAsync
using LibraryHub.Models;
using LibraryHub.Models.ViewModels;

namespace LibraryHub.Controllers
{
    public class ProfileController : Controller
    {
        private readonly DataContext _context;

        public ProfileController(DataContext context) 
        { 
            _context = context; 
        }

        public async Task<IActionResult> Index()
        {
            // 1. Lấy UserID của người dùng hiện tại đang đăng nhập
            var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userIdString)) 
            {
                return RedirectToAction("Index", "Login"); // Chuyển về trang đăng nhập nếu chưa login
            }

            int userId = int.Parse(userIdString);

            // 2. Lấy thông tin Độc giả (Patron) dựa trên UserID
            // Lưu ý: Phải dùng _context.tblPatrons (là DbSet trong DataContext)
            var patron = await _context.Patrons
                .Include(p => p.User)
                .FirstOrDefaultAsync(p => p.UserID == userId);

            if (patron == null) return NotFound();

            // 3. Lấy lịch sử mượn sách của độc giả này
            var history = await _context.Borrowings
                .Where(b => b.PatronID == patron.PatronID)
                .OrderByDescending(b => b.BorrowDate)
                .ToListAsync();

            // 4. Đổ dữ liệu vào ViewModel để hiển thị ra View
            var viewModel = new ProfileViewModel
            {
                Patron = patron,
                BorrowingHistory = history
            };

            return View(viewModel);
        }
    }
}