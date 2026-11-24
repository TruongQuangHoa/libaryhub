using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using LibraryHub.Models;
using LibraryHub.Utilities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace LibraryHub.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly DataContext _context;

        public HomeController(ILogger<HomeController> logger, DataContext context)
        {
            _logger = logger;
            _context = context;
        }

        public async Task<IActionResult> Index(int? clear_cache)
        {
            ViewBag.ClearCache = clear_cache == 1;

            // Tổng số lượt độc giả của các đầu sách đang hiển thị
            ViewBag.TotalBooksBorrowed = await _context.Books
                .Where(b => b.IsActive)
                .SumAsync(b => (int?)b.TotalReaders) ?? 0;

            // Tổng số thể loại đang hoạt động
            ViewBag.TotalCategories = await _context.Categories
                .CountAsync(c => c.IsActive);

            // Tổng số đầu sách đang hiển thị
            ViewBag.TotalBookTitles = await _context.Books
                .CountAsync(b => b.IsActive);

            return View();
        }

        [Route("/book-{slug}-{id:long}.html", Name = "Details")]
        public IActionResult Details(long? id)
        {
            if (id == null) return NotFound();
            var book = _context.viewBooks
                .FirstOrDefault(m => m.BookID == id && m.IsActive == true);
            if (book == null) return NotFound();
            return View(book);
        }

        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            Functions._UserID = 0;
            Functions._UserName = string.Empty;
            Functions._Email = string.Empty;
            Functions._RoleId = string.Empty;
            Functions._Message = "Bạn đã đăng xuất thành công!";

            return RedirectToAction("Index", "Home");
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}