using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using LibraryHub.Utilities;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.Controllers
{
    public class RegisterController : Controller
    {
        private readonly DataContext _context;

        public RegisterController(DataContext context)
        {
            _context = context;
        }

        // GET: Hiển thị form đăng ký
        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(tblUsers auser)
        {
            // 1. Kiểm tra đầu vào cơ bản
            if (auser == null ||
                string.IsNullOrWhiteSpace(auser.UserName) ||
                string.IsNullOrWhiteSpace(auser.Email) ||
                string.IsNullOrWhiteSpace(auser.Password))
            {
                TempData["ErrorMessage"] = "Vui lòng nhập đầy đủ thông tin đăng ký!";
                return View(auser);
            }

            // ==================== KIỂM TRA TRÙNG EMAIL (DUY NHẤT) ====================
            var isEmailExists = await _context.tblUsers
                .AnyAsync(u => u.Email.ToLower() == auser.Email.ToLower());

            if (isEmailExists)
            {
                // Trả về TempData lỗi và return View(auser) để giữ nguyên dữ liệu trong các ô input
                TempData["ErrorMessage"] = "Địa chỉ Email này đã tồn tại! Vui lòng nhập lại Email khác.";
                return View(auser);
            }
            // =========================================================================

            // 2. Kiểm tra quy tắc mật khẩu (ít nhất 4 ký tự + 1 ký tự đặc biệt)
            if (auser.Password.Length < 4 ||
                !Regex.IsMatch(auser.Password, @"[!@#$%^&*(),.?""':{}|<>]"))
            {
                TempData["ErrorMessage"] = "Mật khẩu phải có ít nhất 4 ký tự và chứa ít nhất 1 ký tự đặc biệt!";
                return View(auser);
            }

            // 3. Băm mật khẩu bằng PasswordHasher
            var passwordHasher = new PasswordHasher<tblUsers>();
            auser.Password = passwordHasher.HashPassword(auser, auser.Password);
            auser.IsActive = true;

            // 4. Lưu vào bảng tblUsers
            _context.tblUsers.Add(auser);
            await _context.SaveChangesAsync();

            // 5. Tạo bản ghi độc giả trong tblPatron (Đồng bộ thông tin)
            var newPatron = new tblPatron
            {
                UserID = auser.UserID,
                PatronName = auser.UserName,
                Email = auser.Email,
                RegisterDate = DateTime.Now,
                IsActive = true
            };
            _context.Patrons.Add(newPatron);
            await _context.SaveChangesAsync();

            // 6. Gán vai trò mặc định "Patron" cho người dùng mới
            var patronRole = await _context.tblRoles
                .FirstOrDefaultAsync(r => r.RoleName == "Patron");

            if (patronRole != null)
            {
                var userRoleEntry = new tblUsersRoles
                {
                    UserID = auser.UserID,
                    RoleID = patronRole.RoleID
                };
                _context.tblUsersRoles.Add(userRoleEntry);
                await _context.SaveChangesAsync();
            }

            TempData["SuccessMessage"] = "Đăng ký thành công! Vui lòng dùng Email và mật khẩu để đăng nhập.";
            return RedirectToAction("Index", "Login");
        }
    }
}