using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LibraryHub.Utilities;

namespace LibraryHub.Controllers
{
    public class LoginController : Controller
    {
        private readonly DataContext _context;

        public LoginController(DataContext context)
        {
            _context = context;
        }

        [HttpGet]
        public IActionResult Index()
        {
            // Lấy thông báo từ TempData nếu có (ví dụ từ trang Đăng ký chuyển qua)
            ViewBag.SuccessMessage = TempData["SuccessMessage"];
            ViewBag.ErrorMessage = TempData["ErrorMessage"];
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(tblUsers user, bool remember = false)
        {
            // Ở form đăng nhập, ô nhập liệu "Tên đăng nhập/Email" sẽ map vào thuộc tính user.Email hoặc user.UserName tùy bạn cấu hình.
            // Giả sử form đăng nhập của bạn truyền dữ liệu vào thuộc tính user.Email:
            if (user == null || string.IsNullOrWhiteSpace(user.Email) || string.IsNullOrWhiteSpace(user.Password))
            {
                TempData["ErrorMessage"] = "Vui lòng nhập đầy đủ địa chỉ Email và mật khẩu.";
                return RedirectToAction("Index");
            }

            // ==================== TÌM TÀI KHOẢN BẰNG EMAIL ====================
            var account = await _context.tblUsers
                .FirstOrDefaultAsync(u => u.Email.ToLower() == user.Email.ToLower() && u.IsActive);

            if (account == null)
            {
                ViewBag.ErrorMessage = "Địa chỉ Email không tồn tại hoặc tài khoản đã bị khóa.";
                return View(user); // Giữ nguyên input để người dùng sửa
            }
            // =================================================================

            // Xác thực mật khẩu
            var passwordHasher = new PasswordHasher<tblUsers>();
            var verificationResult = passwordHasher.VerifyHashedPassword(account, account.Password, user.Password);

            if (verificationResult == PasswordVerificationResult.Failed)
            {
                TempData["ErrorMessage"] = "Mật khẩu không chính xác.";
                return View(user);
            }

            if (verificationResult == PasswordVerificationResult.SuccessRehashNeeded)
            {
                account.Password = passwordHasher.HashPassword(account, user.Password);
                _context.Update(account);
                await _context.SaveChangesAsync();
            }

            // Lấy danh sách các vai trò (Roles) dựa trên UserID đã tìm thấy từ Email duy nhất
            var userRoles = await _context.tblUsersRoles
                .Where(ur => ur.UserID == account.UserID)
                .Include(ur => ur.Role)
                .Select(ur => ur.Role.RoleName)
                .ToListAsync();

            // Thiết lập các Claims cho Cookie Authentication
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, account.UserID.ToString()),
                new Claim(ClaimTypes.Name, account.UserName),
                new Claim(ClaimTypes.Email, account.Email)
            };

            foreach (var role in userRoles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new AuthenticationProperties();


            bool isAdmin = userRoles.Any(r =>
                r.Equals("Admin", StringComparison.OrdinalIgnoreCase));

            bool isLibrarian = userRoles.Any(r =>
                r.Equals("Librarian", StringComparison.OrdinalIgnoreCase));


            if (isAdmin || isLibrarian)
            {
                // Phiên quản trị không lưu đăng nhập lâu dài.
                authProperties.IsPersistent = false;

                // Cookie được gia hạn khi có yêu cầu hợp lệ.
                // Thời gian không hoạt động được kiểm soát riêng bằng Session
                // và bộ đếm trên giao diện quản trị.
                authProperties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10);
            }
            else
            {
                // Giữ nguyên chính sách đăng nhập của Patron.
                authProperties.IsPersistent = remember;
                authProperties.ExpiresUtc = remember
                    ? DateTimeOffset.UtcNow.AddDays(7)
                    : DateTimeOffset.UtcNow.AddHours(2);
            }

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);

            if (isAdmin || isLibrarian)
            {
                HttpContext.Session.SetString(
                    "ManagementLastActivityUtc",
                    DateTimeOffset.UtcNow.ToString("O"));
            }

            // Lưu trạng thái vào lớp trợ giúp Functions
            // Functions._Message = $"Chào mừng {account.UserName}!";
            // Functions._UserID = account.UserID;
            // Functions._UserName = account.UserName;
            // Functions._Email = account.Email;
            // Functions._RoleId = userRoles.FirstOrDefault() ?? "Guest";


            // Xử lý giữ luồng mượn sách
            if (!isAdmin && !isLibrarian && TempData["BookID"] != null)
            {
                TempData["SuccessMessage"] =
                    "Đăng nhập thành công! Bạn có thể tiếp tục mượn sách.";

                return RedirectToAction("Cart", "BorrowingCart");
            }


            if (isAdmin || isLibrarian)
            {
                // Admin và Librarian vào khu vực quản trị
                return RedirectToAction(
                    "Index",
                    "Home",
                    new { area = "Admin" }
                );
            }

            // Patron tiếp tục vào giao diện độc giả
            return RedirectToAction("Index", "Home", new { area = "" });
        }



        [HttpGet]
        public async Task<IActionResult> Logout(bool idle = false)
        {
            // Xóa cookie đăng nhập hiện tại
            await HttpContext.SignOutAsync(
                CookieAuthenticationDefaults.AuthenticationScheme);

            // Xóa toàn bộ dữ liệu session
            HttpContext.Session.Clear();

            // Xóa cookie session trên trình duyệt
            HttpContext.Response.Cookies.Delete(".AspNetCore.Session");

            if (idle)
            {
                TempData["ErrorMessage"] =
                    "Phiên đăng nhập đã kết thúc do không hoạt động. Vui lòng đăng nhập lại.";
            }

            // Trở về giao diện Patron nhưng không còn đăng nhập
            return RedirectToAction("Index", "Home", new { area = "" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LogoutPost()
        {
            return await Logout();
        }

        [HttpPost]
        public IActionResult KeepAlive()
        {
            bool isManagementUser =
                User.Identity?.IsAuthenticated == true &&
                (User.IsInRole("Admin") || User.IsInRole("Librarian"));

            if (!isManagementUser)
                return Unauthorized();

            HttpContext.Session.SetString(
                "ManagementLastActivityUtc",
                DateTimeOffset.UtcNow.ToString("O"));

            return NoContent();
        }
    }
}