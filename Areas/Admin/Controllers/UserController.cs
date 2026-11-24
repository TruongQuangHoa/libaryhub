using System;
using System.Linq;
using System.Text.RegularExpressions;
using LibraryHub.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class UserController : Controller
    {
        private readonly DataContext _context;

        public UserController(DataContext context)
        {
            _context = context;
        }

        public IActionResult Index()
        {
            var hslist = _context.tblUsers.OrderBy(h => h.UserID).ToList();
            return View(hslist);
        }

        public IActionResult Delete(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (currentUserId == id.ToString())
            {
                TempData["Error"] = "Bạn không thể xóa tài khoản đang đăng nhập.";
                return RedirectToAction(nameof(Index));
            }

            var hs = _context.tblUsers.Find(id);

            if (hs == null)
                return NotFound();

            return View(hs);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            // =========================================================
            // KHÔNG CHO XÓA TÀI KHOẢN ĐANG ĐĂNG NHẬP
            // =========================================================
            var currentUserId =
                User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (currentUserId == id.ToString())
            {
                TempData["Error"] =
                    "Bạn không thể xóa tài khoản đang đăng nhập.";

                return RedirectToAction(nameof(Index));
            }

            // =========================================================
            // TÌM USER
            // =========================================================
            var user = await _context.tblUsers
                .FirstOrDefaultAsync(u => u.UserID == id);

            if (user == null)
                return NotFound();

            await using var transaction =
                await _context.Database.BeginTransactionAsync();

            try
            {
                // =====================================================
                // 1. XÓA PATRON LIÊN KẾT
                // =====================================================
                var patron = await _context.Patrons
                    .FirstOrDefaultAsync(p => p.UserID == id);

                if (patron != null)
                {
                    _context.Patrons.Remove(patron);
                }

                // =====================================================
                // 2. XÓA LIBRARIAN LIÊN KẾT
                // =====================================================
                var librarian = await _context.Librarians
                    .FirstOrDefaultAsync(l => l.UserID == id);

                if (librarian != null)
                {
                    _context.Librarians.Remove(librarian);
                }

                // =====================================================
                // 3. XÓA TẤT CẢ USER ROLE
                // =====================================================
                var userRoles = await _context.tblUsersRoles
                    .Where(ur => ur.UserID == id)
                    .ToListAsync();

                if (userRoles.Any())
                {
                    _context.tblUsersRoles.RemoveRange(userRoles);
                }

                // =====================================================
                // 4. XÓA USER
                // =====================================================
                _context.tblUsers.Remove(user);

                // =====================================================
                // 5. SAVE
                // =====================================================
                await _context.SaveChangesAsync();

                // =====================================================
                // 6. COMMIT
                // =====================================================
                await transaction.CommitAsync();

                TempData["Success"] =
                    "Đã xóa tài khoản và toàn bộ dữ liệu liên kết thành công.";

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();

                TempData["Error"] =
                    "Không thể xóa tài khoản: " + ex.Message;

                return RedirectToAction(nameof(Index));
            }
        }


        [HttpGet]
        public IActionResult Create()
        {
            LoadUserDropdown();
            return View();
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(tblUsers mn)
        {
            // Chuẩn hóa dữ liệu
            mn.UserName = mn.UserName?.Trim() ?? string.Empty;
            mn.Email = mn.Email?.Trim() ?? string.Empty;

            // Chỉ kiểm tra email trùng, không phân biệt chữ hoa/chữ thường
            if (!string.IsNullOrWhiteSpace(mn.Email))
            {
                bool duplicateEmail = await _context.tblUsers
                    .AnyAsync(u => u.Email != null &&
                        u.Email.Trim().ToLower() == mn.Email.ToLower());

                if (duplicateEmail)
                {
                    ModelState.AddModelError(
                        nameof(mn.Email),
                        "Email này đã được sử dụng. Vui lòng nhập email khác.");
                }
            }

            // Kiểm tra mật khẩu
            if (!IsValidPassword(mn.Password))
            {
                ModelState.AddModelError(
                    nameof(mn.Password),
                    "Mật khẩu phải có ít nhất 4 ký tự và chứa ít nhất 1 ký tự đặc biệt.");
            }

            // Có lỗi thì hiển thị lại form
            if (!ModelState.IsValid)
            {
                LoadUserDropdown();
                return View(mn);
            }

            // Mã hóa mật khẩu
            var passwordHasher = new PasswordHasher<tblUsers>();
            mn.Password = passwordHasher.HashPassword(mn, mn.Password);

            // Lưu người dùng
            _context.tblUsers.Add(mn);
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }



        [HttpGet]
        public IActionResult Edit(int? id)
        {
            if (id == null || id <= 0)
                return NotFound();

            var mn = _context.tblUsers.Find(id);

            if (mn == null)
                return NotFound();

            // Không đưa mật khẩu đã mã hóa ra form chỉnh sửa
            mn.Password = string.Empty;

            LoadUserDropdown();
            return View(mn);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(tblUsers mn)
        {
            var acc = _context.tblUsers
                .FirstOrDefault(u => u.UserID == mn.UserID);

            if (acc == null)
                return NotFound();

            // Chỉ kiểm tra email trùng với tài khoản khác.
            if (!string.IsNullOrWhiteSpace(mn.Email))
            {
                bool emailExists = _context.tblUsers.Any(u =>
                    u.UserID != mn.UserID &&
                    u.Email != null &&
                    u.Email.Trim().ToLower() == mn.Email.Trim().ToLower());

                if (emailExists)
                {
                    ModelState.AddModelError(
                        nameof(mn.Email),
                        "Email này đã được sử dụng bởi tài khoản khác.");
                }
            }

            // Chỉ kiểm tra mật khẩu khi người dùng nhập mật khẩu mới.
            if (!string.IsNullOrWhiteSpace(mn.Password))
            {
                if (!IsValidPassword(mn.Password))
                {
                    ModelState.AddModelError(
                        nameof(mn.Password),
                        "Mật khẩu phải có ít nhất 4 ký tự và chứa ít nhất 1 ký tự đặc biệt.");
                }
            }

            if (!ModelState.IsValid)
            {
                LoadUserDropdown();
                mn.Password = string.Empty;
                return View(mn);
            }

            // Cập nhật thông tin cơ bản.
            acc.UserName = mn.UserName;
            acc.Email = mn.Email?.Trim();
            acc.IsActive = mn.IsActive;

            // Chỉ mã hóa và cập nhật nếu có mật khẩu mới.
            if (!string.IsNullOrWhiteSpace(mn.Password))
            {
                var passwordHasher = new PasswordHasher<tblUsers>();

                acc.Password = passwordHasher.HashPassword(
                    acc,
                    mn.Password);
            }

            // Đồng bộ thông tin với Patron nếu có liên kết.
            var patron = _context.Patrons
                .FirstOrDefault(p => p.UserID == acc.UserID);

            if (patron != null)
            {
                patron.PatronName = acc.UserName;
                patron.Email = acc.Email;
                patron.IsActive = acc.IsActive;
            }

            // Đồng bộ thông tin với Librarian nếu có liên kết.
            var librarian = _context.Librarians
                .FirstOrDefault(l => l.UserID == acc.UserID);

            if (librarian != null)
            {
                librarian.FullName = acc.UserName;
                librarian.Email = acc.Email;
                librarian.IsActive = acc.IsActive;
            }

            _context.SaveChanges();

            return RedirectToAction(nameof(Index));
        }



        // ================== Helper Methods ==================

        private bool IsValidPassword(string password)
        {
            return !string.IsNullOrEmpty(password)
                && password.Length >= 4
                && Regex.IsMatch(password, @"[!@#$%^&*(),.?""':{}|<>]");
        }

        private void LoadUserDropdown()
        {
            var mnList = _context.tblUsers.Select(m => new SelectListItem
            {
                Text = (m.UserID == 1) ? m.UserName : "-- " + m.UserID,
                Value = m.UserID.ToString()
            }).ToList();

            mnList.Insert(0, new SelectListItem
            {
                Text = "--- select ---",
                Value = "0"
            });

            ViewBag.mnList = mnList;
        }

        public IActionResult ThongKe()
        {
            var totalUsers = _context.tblUsers.Count();
            var activeUsers = _context.tblUsers.Count(u => u.IsActive);
            var inactiveUsers = totalUsers - activeUsers;

            ViewBag.TotalUsers = totalUsers;
            ViewBag.ActiveUsers = activeUsers;
            ViewBag.InactiveUsers = inactiveUsers;

            return View(); // Views/Admin/User/ThongKe.cshtml
        }

        public IActionResult UserListPartial(bool? isActive)
        {
            IQueryable<tblUsers> usersQuery = _context.tblUsers;

            if (isActive.HasValue)
            {
                usersQuery = usersQuery.Where(u => u.IsActive == isActive.Value);
            }

            var users = usersQuery.ToList();

            // Kiểm tra xem dữ liệu có được lấy ra không
            if (users != null && users.Any())
            {
                // Trả về dữ liệu JSON
                return Json(users.Select(u => new
                {
                    u.UserID,
                    u.UserName,
                    u.Email,
                    u.IsActive
                }));
            }
            else
            {
                return Json(new List<object>()); // Trả về mảng rỗng nếu không có dữ liệu
            }
        }

    }
}