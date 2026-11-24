using System;
using System.Linq;
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class UserRoleController : Controller
    {
        private readonly DataContext _context;

        public UserRoleController(DataContext context)
        {
            _context = context;
        }

        // =========================================================
        // DANH SÁCH VAI TRÒ NGƯỜI DÙNG
        // =========================================================
        public IActionResult Index()
        {
            var hslist = _context.tblUsersRoles
                .Include(h => h.User)
                .Include(h => h.Role)
                .OrderBy(h => h.UserID)
                .ToList();

            return View(hslist);
        }

        // =========================================================
        // DELETE - GET
        // =========================================================
        public IActionResult Delete(int? userID, int? roleID)
        {
            if (userID == null || roleID == null)
                return NotFound();

            // Không cho phép tự xóa vai trò của chính mình
            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (currentUserId == userID.ToString())
            {
                TempData["Error"] = "Bạn không được xóa vai trò của chính mình.";
                return RedirectToAction(nameof(Index));
            }

            var hs = _context.tblUsersRoles
                .Include(h => h.User)
                .Include(h => h.Role)
                .FirstOrDefault(x =>
                    x.UserID == userID &&
                    x.RoleID == roleID);

            if (hs == null)
                return NotFound();

            return View(hs);
        }

        // =========================================================
        // DELETE - POST
        // =========================================================

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteConfirmed(int userID, int roleID)
        {
            // Không cho phép tự xóa vai trò của chính mình
            var currentUserId = User.FindFirst(
                ClaimTypes.NameIdentifier)?.Value;

            if (currentUserId == userID.ToString())
            {
                TempData["Error"] =
                    "Bạn không được xóa vai trò của chính mình.";

                return RedirectToAction(nameof(Index));
            }

            var delhs = _context.tblUsersRoles
                .Include(ur => ur.Role)
                .FirstOrDefault(x =>
                    x.UserID == userID &&
                    x.RoleID == roleID);

            if (delhs == null)
                return NotFound();

            string deletedRoleName = delhs.Role?.RoleName ?? "";

            // Xóa liên kết User - Role
            _context.tblUsersRoles.Remove(delhs);
            _context.SaveChanges();

            // Nếu xóa vai trò Librarian thì xóa hồ sơ thủ thư tương ứng
            if (deletedRoleName == "Librarian")
            {
                var librarian = _context.Librarians
                    .FirstOrDefault(l => l.UserID == userID);

                if (librarian != null)
                {
                    _context.Librarians.Remove(librarian);
                    _context.SaveChanges();
                }
            }

            // Nếu xóa vai trò Patron thì xử lý hồ sơ Patron
            RemoveOrphanedPatron(userID, deletedRoleName);

            return RedirectToAction(nameof(Index));
        }


        // =========================================================
        // XÓA PATRON KHI USER KHÔNG CÒN ROLE PATRON
        // =========================================================
        private void RemoveOrphanedPatron(
    int userId,
    string deletedRoleName)
        {
            // Chỉ xử lý khi role bị xóa là Patron
            if (deletedRoleName != "Patron")
                return;

            // Kiểm tra User còn role Patron hay không
            bool stillHasPatronRole =
                _context.tblUsersRoles
                    .Include(ur => ur.Role)
                    .Any(ur =>
                        ur.UserID == userId &&
                        ur.Role != null &&
                        ur.Role.RoleName == "Patron");

            // Nếu vẫn còn Patron thì không xóa
            if (stillHasPatronRole)
                return;

            // Tìm Patron bằng UserID
            var patron = _context.Patrons
                .FirstOrDefault(p => p.UserID == userId);

            if (patron != null)
            {
                _context.Patrons.Remove(patron);
                _context.SaveChanges();
            }
        }
        // =========================================================
        // CREATE - GET
        // =========================================================
        public IActionResult Create()
        {
            LoadData();
            return View();
        }

        // =========================================================
        // CREATE - POST
        // =========================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(tblUsersRoles mn)
        {
            // Kiểm tra người dùng có tồn tại và đang hoạt động không
            var user = _context.tblUsers
                .FirstOrDefault(u => u.UserID == mn.UserID);

            if (user == null || !user.IsActive)
            {
                ModelState.AddModelError(
                    nameof(mn.UserID),
                    "Người dùng không tồn tại hoặc đã bị vô hiệu hóa."
                );

                LoadData();
                return View(mn);
            }

            // Người dùng đã có bất kỳ vai trò nào thì không được thêm tiếp
            bool userHasRole = _context.tblUsersRoles
                .Any(ur => ur.UserID == mn.UserID);

            if (userHasRole)
            {
                ModelState.AddModelError(
                    nameof(mn.UserID),
                    "Người dùng này đã có vai trò. Vui lòng chọn người dùng khác."
                );

                LoadData();
                return View(mn);
            }

            if (!ModelState.IsValid)
            {
                LoadData();
                return View(mn);
            }

            _context.tblUsersRoles.Add(mn);
            _context.SaveChanges();

            // Đồng bộ dữ liệu theo vai trò
            SyncUserToRole(mn.UserID, mn.RoleID);

            return RedirectToAction(nameof(Index));
        }


        // =========================================================
        // EDIT - GET
        // =========================================================
        [HttpGet]
        public IActionResult Edit(int userID, int roleID)
        {
            var currentUserId = User.FindFirst(
                ClaimTypes.NameIdentifier)?.Value;

            if (currentUserId == userID.ToString())
            {
                TempData["Error"] =
                    "Bạn không được thay đổi vai trò của chính mình.";

                return RedirectToAction(nameof(Index));
            }

            var dp = _context.tblUsersRoles
                .Include(x => x.User)
                .Include(x => x.Role)
                .FirstOrDefault(x =>
                    x.UserID == userID &&
                    x.RoleID == roleID);

            if (dp == null)
                return NotFound();

            ViewBag.UserName = dp.User?.UserName;

            LoadData(userID, roleID);

            return View(dp);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(tblUsersRoles dp, int oldRoleID)
        {
            // Không cho phép tự thay đổi vai trò của chính mình
            var currentUserId = User.FindFirst(
                ClaimTypes.NameIdentifier)?.Value;

            if (currentUserId == dp.UserID.ToString())
            {
                TempData["Error"] =
                    "Bạn không được thay đổi vai trò của chính mình.";

                return RedirectToAction(nameof(Index));
            }

            // Lấy bản ghi vai trò cũ
            var oldRecord = _context.tblUsersRoles
                .Include(x => x.Role)
                .FirstOrDefault(x =>
                    x.UserID == dp.UserID &&
                    x.RoleID == oldRoleID);

            if (oldRecord == null)
                return NotFound();

            // Kiểm tra vai trò mới có tồn tại không
            var newRole = _context.tblRoles
                .FirstOrDefault(r => r.RoleID == dp.RoleID);

            if (newRole == null)
            {
                ModelState.AddModelError(
                    nameof(dp.RoleID),
                    "Vai trò được chọn không tồn tại.");
            }

            // Không cho chọn trùng vai trò đã được gán
            bool exists = _context.tblUsersRoles.Any(x =>
                x.UserID == dp.UserID &&
                x.RoleID == dp.RoleID &&
                x.RoleID != oldRoleID);

            if (exists)
            {
                ModelState.AddModelError(
                    nameof(dp.RoleID),
                    "Người dùng này đã có vai trò đó rồi.");
            }

            if (!ModelState.IsValid)
            {
                var currentRecord = _context.tblUsersRoles
                    .Include(x => x.User)
                    .FirstOrDefault(x =>
                        x.UserID == dp.UserID &&
                        x.RoleID == oldRoleID);

                ViewBag.UserName = currentRecord?.User?.UserName;

                LoadData(dp.UserID, oldRoleID);
                return View(dp);
            }

            string oldRoleName = oldRecord.Role?.RoleName ?? "";
            string newRoleName = newRole!.RoleName;

            // Nếu không thay đổi vai trò thì không cần xử lý
            if (oldRoleID == dp.RoleID)
                return RedirectToAction(nameof(Index));

            // Thực hiện thay đổi vai trò và hồ sơ trong cùng transaction
            using var transaction = _context.Database.BeginTransaction();

            try
            {
                // Xóa vai trò cũ
                _context.tblUsersRoles.Remove(oldRecord);

                // Thêm vai trò mới
                _context.tblUsersRoles.Add(new tblUsersRoles
                {
                    UserID = dp.UserID,
                    RoleID = dp.RoleID
                });

                // Lưu thay đổi vai trò trước để kiểm tra vai trò còn lại
                _context.SaveChanges();

                // Xóa hồ sơ thuộc vai trò cũ nếu không còn vai trò đó
                RemoveOrphanedRoleProfile(dp.UserID, oldRoleName);

                // Lưu việc xóa hồ sơ cũ
                _context.SaveChanges();

                // Tạo hồ sơ thuộc vai trò mới nếu chưa tồn tại
                SyncUserToRole(dp.UserID, dp.RoleID);

                transaction.Commit();

                return RedirectToAction(nameof(Index));
            }
            catch
            {
                transaction.Rollback();

                // Đọc lại dữ liệu sau khi rollback
                _context.ChangeTracker.Clear();

                TempData["Error"] =
                    "Không thể cập nhật vai trò người dùng. Vui lòng thử lại.";

                return RedirectToAction(nameof(Index));
            }
        }

        // =========================================================
        // ĐỒNG BỘ USER VỚI PATRON
        // =========================================================
        //
        // QUAN TRỌNG:
        //
        // Admin KHÔNG tạo Librarian.
        //
        // Admin chỉ tồn tại trong:
        //
        //     tblUsers
        //     tblUsersRoles
        //
        // Patron mới được đồng bộ sang:
        //
        //     tblPatron
        //
        // Librarian được quản lý riêng tại LibrarianController.
        // =========================================================

        private void SyncUserToRole(int userId, int roleId)
        {
            var role = _context.tblRoles
                .FirstOrDefault(r => r.RoleID == roleId);

            if (role == null)
                return;

            var user = _context.tblUsers
                .FirstOrDefault(u => u.UserID == userId);

            if (user == null)
                return;

            // =====================================================
            // PATRON
            // =====================================================
            if (role.RoleName == "Patron")
            {
                var patronExists = _context.Patrons
                    .Any(p => p.UserID == userId);

                if (!patronExists)
                {
                    var patron = new tblPatron
                    {
                        UserID = user.UserID,
                        PatronName = user.UserName,
                        Email = user.Email,
                        RegisterDate = DateTime.Now,
                        IsActive = user.IsActive
                    };

                    _context.Patrons.Add(patron);
                    _context.SaveChanges();
                }

                return;
            }

            // =====================================================
            // LIBRARIAN
            // =====================================================
            if (role.RoleName == "Librarian")
            {
                var librarianExists = _context.Librarians
                    .Any(l => l.UserID == userId);

                if (!librarianExists)
                {
                    var librarian = new tblLibrarian
                    {
                        UserID = user.UserID,
                        FullName = user.UserName,
                        Email = user.Email,
                        IsActive = user.IsActive
                    };

                    _context.Librarians.Add(librarian);
                    _context.SaveChanges();
                }

                return;
            }

            // =====================================================
            // ADMIN
            // =====================================================
            if (role.RoleName == "Admin")
            {
                // Admin không tạo Patron
                // Admin không tạo Librarian
                return;
            }
        }

        // =========================================================
        // LOAD DATA
        // =========================================================

        private void LoadData(int? userID = null, int? roleID = null)
        {
            // ============================================
            // DROPDOWN NGƯỜI DÙNG
            // ============================================
            var usersWithRoles = _context.tblUsersRoles
                .Select(ur => ur.UserID);

            var activeUsers = _context.tblUsers
                .Where(u => u.IsActive)
                .Where(u => !usersWithRoles.Contains(u.UserID)
                            || u.UserID == userID)
                .OrderBy(u => u.UserID)
                .ToList();

            ViewBag.listUser = new SelectList(
                activeUsers,
                "UserID",
                "UserName",
                userID
            );

            // ============================================
            // DROPDOWN VAI TRÒ
            // ============================================
            var roles = _context.tblRoles
                .Where(r =>
                    // Giữ vai trò hiện tại khi đang sửa
                    (roleID.HasValue && r.RoleID == roleID.Value)
                    ||
                    // Hoặc vai trò chưa được gán cho người dùng
                    (
                        userID.HasValue
                        && !_context.tblUsersRoles.Any(ur =>
                            ur.UserID == userID.Value
                            && ur.RoleID == r.RoleID)
                    )
                    ||
                    // Khi thêm mới, lấy toàn bộ vai trò
                    !userID.HasValue
                )
                .OrderBy(r => r.RoleID)
                .ToList();

            ViewBag.listRole = new SelectList(
                roles,
                "RoleID",
                "RoleName",
                roleID
            );
        }

        // =========================================================
        // XÓA HỒ SƠ CŨ KHI NGƯỜI DÙNG KHÔNG CÒN VAI TRÒ TƯƠNG ỨNG
        // =========================================================
        private void RemoveOrphanedRoleProfile(int userId, string deletedRoleName)
        {
            // Nếu xóa vai trò Patron
            if (deletedRoleName == "Patron")
            {
                bool stillHasPatronRole = _context.tblUsersRoles
                    .Include(ur => ur.Role)
                    .Any(ur =>
                        ur.UserID == userId &&
                        ur.Role != null &&
                        ur.Role.RoleName == "Patron");

                if (!stillHasPatronRole)
                {
                    var patron = _context.Patrons
                        .FirstOrDefault(p => p.UserID == userId);

                    if (patron != null)
                    {
                        _context.Patrons.Remove(patron);
                    }
                }
            }

            // Nếu xóa vai trò Librarian
            else if (deletedRoleName == "Librarian")
            {
                bool stillHasLibrarianRole = _context.tblUsersRoles
                    .Include(ur => ur.Role)
                    .Any(ur =>
                        ur.UserID == userId &&
                        ur.Role != null &&
                        ur.Role.RoleName == "Librarian");

                if (!stillHasLibrarianRole)
                {
                    var librarian = _context.Librarians
                        .FirstOrDefault(l => l.UserID == userId);

                    if (librarian != null)
                    {
                        _context.Librarians.Remove(librarian);
                    }
                }
            }
        }
    }
}