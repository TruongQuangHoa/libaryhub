using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.IO;
using ClosedXML.Excel;
using System.Globalization;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin, Librarian")]
    public class PatronController : Controller
    {
        private readonly DataContext _context;
        private readonly IWebHostEnvironment _environment;

        public PatronController(
            DataContext context,
            IWebHostEnvironment environment)
        {
            _context = context;
            _environment = environment;
        }

        public IActionResult Index()
        {
            var mnList = _context.Patrons.OrderBy(m => m.PatronID).ToList();
            return View(mnList);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportExcel(IFormFile? excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                TempData["ImportError"] = "Vui lòng chọn file Excel.";
                return RedirectToAction(nameof(Index));
            }

            if (!Path.GetExtension(excelFile.FileName)
                .Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ImportError"] = "Chỉ chấp nhận file Excel .xlsx.";
                return RedirectToAction(nameof(Index));
            }

            if (excelFile.Length > 10 * 1024 * 1024)
            {
                TempData["ImportError"] = "File Excel không được vượt quá 10 MB.";
                return RedirectToAction(nameof(Index));
            }

            var errors = new List<string>();

            try
            {
                using var stream = new MemoryStream();
                await excelFile.CopyToAsync(stream);
                stream.Position = 0;

                using var workbook = new XLWorkbook(stream);

                var worksheet = workbook.Worksheets
                    .FirstOrDefault(s => s.Name == "DocGia")
                    ?? workbook.Worksheets.First();

                var usedRange = worksheet.RangeUsed();

                if (usedRange == null)
                {
                    TempData["ImportError"] = "File Excel không có dữ liệu.";
                    return RedirectToAction(nameof(Index));
                }

                // Đọc tên cột ở hàng đầu tiên.
                var headers = new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var cell in worksheet.Row(1).CellsUsed())
                {
                    var header = cell.GetString().Trim();

                    if (!string.IsNullOrWhiteSpace(header))
                        headers[header] = cell.Address.ColumnNumber;
                }

                string[] requiredHeaders =
                {
            "PatronName",
            "Address",
            "Phone",
            "Email",
            "RegisterDate",
            "IsActive"
        };

                var missingHeaders = requiredHeaders
                    .Where(h => !headers.ContainsKey(h))
                    .ToList();

                if (missingHeaders.Any())
                {
                    TempData["ImportError"] =
                        "Thiếu các cột: " + string.Join(", ", missingHeaders);

                    return RedirectToAction(nameof(Index));
                }

                string GetText(IXLRow row, string column)
                {
                    var cell = row.Cell(headers[column]);

                    return cell.IsEmpty()
                        ? string.Empty
                        : cell.GetFormattedString().Trim();
                }

                bool TryReadDate(IXLRow row, out DateTime? date)
                {
                    date = null;
                    var cell = row.Cell(headers["RegisterDate"]);

                    if (cell.IsEmpty())
                        return true;

                    if (cell.TryGetValue<DateTime>(out var excelDate))
                    {
                        date = excelDate;
                        return excelDate.Date <= DateTime.Today;
                    }

                    var value = cell.GetString().Trim();

                    if (DateTime.TryParseExact(
                        value,
                        new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd" },
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var parsedDate)
                        && parsedDate.Date <= DateTime.Today)
                    {
                        date = parsedDate;
                        return true;
                    }

                    return false;
                }

                bool TryReadActive(IXLRow row, out bool isActive)
                {
                    isActive = true;
                    var value = GetText(row, "IsActive");

                    if (string.IsNullOrWhiteSpace(value))
                        return true;

                    if (bool.TryParse(value, out isActive))
                        return true;

                    if (value == "1" ||
                        value.Equals("Có", StringComparison.OrdinalIgnoreCase))
                    {
                        isActive = true;
                        return true;
                    }

                    if (value == "0" ||
                        value.Equals("Không", StringComparison.OrdinalIgnoreCase))
                    {
                        isActive = false;
                        return true;
                    }

                    return false;
                }

                var pending = new List<tblPatron>();
                var emailsInFile = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

                int lastRow = usedRange.RangeAddress.LastAddress.RowNumber;

                for (int rowNumber = 2; rowNumber <= lastRow; rowNumber++)
                {
                    var row = worksheet.Row(rowNumber);

                    if (row.CellsUsed().All(c =>
                        string.IsNullOrWhiteSpace(c.GetString())))
                    {
                        continue;
                    }

                    var name = GetText(row, "PatronName");
                    var email = GetText(row, "Email");

                    if (string.IsNullOrWhiteSpace(name))
                        errors.Add($"Dòng {rowNumber}: Thiếu tên độc giả.");

                    if (string.IsNullOrWhiteSpace(email) ||
                        !new EmailAddressAttribute().IsValid(email))
                    {
                        errors.Add($"Dòng {rowNumber}: Email không hợp lệ.");
                    }
                    else if (!emailsInFile.Add(email))
                    {
                        errors.Add($"Dòng {rowNumber}: Email {email} bị trùng trong file.");
                    }

                    if (!TryReadDate(row, out var registerDate))
                        errors.Add($"Dòng {rowNumber}: Ngày đăng ký không hợp lệ hoặc ở tương lai.");

                    if (!TryReadActive(row, out var isActive))
                        errors.Add($"Dòng {rowNumber}: IsActive chỉ nhận true/false, 1/0, Có/Không.");

                    // Chỉ đưa dòng hợp lệ vào danh sách chờ.
                    if (string.IsNullOrWhiteSpace(name) ||
                        string.IsNullOrWhiteSpace(email) ||
                        !new EmailAddressAttribute().IsValid(email) ||
                        !TryReadDate(row, out registerDate) ||
                        !TryReadActive(row, out isActive))
                    {
                        continue;
                    }

                    pending.Add(new tblPatron
                    {
                        PatronName = name,
                        Address = GetText(row, "Address"),
                        Phone = GetText(row, "Phone"),
                        Email = email,
                        RegisterDate = registerDate,
                        IsActive = isActive,
                        ImagePatron = null
                    });
                }

                if (!pending.Any() && !errors.Any())
                    errors.Add("File không có dòng độc giả hợp lệ.");

                // Kiểm tra email đã tồn tại trong tài khoản hoặc độc giả.
                var existingUserEmails = await _context.tblUsers
                    .Where(u => u.Email != null)
                    .Select(u => u.Email!)
                    .ToListAsync();

                var existingPatronEmails = await _context.Patrons
                    .Where(p => p.Email != null)
                    .Select(p => p.Email!)
                    .ToListAsync();

                var existingEmails = new HashSet<string>(
                    existingUserEmails.Concat(existingPatronEmails),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var patron in pending)
                {
                    if (existingEmails.Contains(patron.Email!))
                    {
                        errors.Add(
                            $"Email {patron.Email} đã tồn tại trong hệ thống.");
                    }
                }

                if (errors.Any())
                {
                    TempData["ImportError"] =
                        "Không thể nhập dữ liệu. Vui lòng sửa các lỗi trong file Excel.";

                    TempData["ImportDetails"] =
                        string.Join("\n", errors.Take(100));

                    return RedirectToAction(nameof(Index));
                }

                // Tìm vai trò Patron.
                var patronRole = await _context.tblRoles
                    .FirstOrDefaultAsync(r => r.RoleName == "Patron");

                if (patronRole == null)
                {
                    TempData["ImportError"] =
                        "Không tìm thấy vai trò Patron trong hệ thống.";

                    return RedirectToAction(nameof(Index));
                }

                // Lưu tài khoản, vai trò và độc giả trong cùng transaction.
                await using var transaction =
                    await _context.Database.BeginTransactionAsync();

                try
                {
                    var passwordHasher = new PasswordHasher<tblUsers>();
                    var users = new List<tblUsers>();

                    foreach (var patron in pending)
                    {
                        var user = new tblUsers
                        {
                            UserName = patron.PatronName,
                            Email = patron.Email,
                            IsActive = patron.IsActive
                        };

                        user.Password = passwordHasher.HashPassword(
                            user, "Patron@123");

                        users.Add(user);
                    }

                    _context.tblUsers.AddRange(users);
                    await _context.SaveChangesAsync();

                    var userRoles = new List<tblUsersRoles>();

                    for (int i = 0; i < pending.Count; i++)
                    {
                        pending[i].UserID = users[i].UserID;

                        userRoles.Add(new tblUsersRoles
                        {
                            UserID = users[i].UserID,
                            RoleID = patronRole.RoleID
                        });
                    }

                    _context.Patrons.AddRange(pending);
                    _context.tblUsersRoles.AddRange(userRoles);

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    TempData["ImportSuccess"] =
                        $"Nhập thành công {pending.Count} độc giả.";

                    return RedirectToAction(nameof(Index));
                }
                catch
                {
                    await transaction.RollbackAsync();

                    TempData["ImportError"] =
                        "Có lỗi khi lưu dữ liệu. Toàn bộ giao dịch đã được hoàn tác.";

                    return RedirectToAction(nameof(Index));
                }
            }
            catch
            {
                TempData["ImportError"] =
                    "Không thể đọc file Excel. Vui lòng kiểm tra định dạng và nội dung file.";

                return RedirectToAction(nameof(Index));
            }
        }


        [HttpGet]
        public IActionResult DownloadExcelTemplate()
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("DocGia");

            string[] headers =
            {
        "PatronName",
        "Address",
        "Phone",
        "Email",
        "RegisterDate",
        "IsActive"
    };

            for (int i = 0; i < headers.Length; i++)
                worksheet.Cell(1, i + 1).Value = headers[i];

            var headerRange = worksheet.Range(1, 1, 1, headers.Length);

            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightBlue;
            headerRange.Style.Alignment.Horizontal =
                XLAlignmentHorizontalValues.Center;

            // Dòng minh họa — hãy thay bằng dữ liệu thật trước khi nhập.
            worksheet.Cell(2, 1).Value = "Nguyễn Văn A";
            worksheet.Cell(2, 2).Value = "Xóm 1, Xã A, Tỉnh B";
            worksheet.Cell(2, 3).Value = "0912345678";
            worksheet.Cell(2, 4).Value = "nguyenvana@example.com";
            worksheet.Cell(2, 5).Value = "15/05/2026";
            worksheet.Cell(2, 6).Value = "true";

            worksheet.Columns().AdjustToContents();
            worksheet.SheetView.FreezeRows(1);

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            return File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "Mau_Nhap_Doc_Gia.xlsx");
        }

        public IActionResult Delete(int? id)
        {
            if (id == null || id == 0)
                return NotFound();
            var mn = _context.Patrons.Find(id);
            if (mn == null)
                return NotFound();
            return View(mn);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var delPatron = await _context.Patrons
                .FirstOrDefaultAsync(p => p.PatronID == id);

            if (delPatron == null)
                return NotFound();

            // =========================================================
            // KIỂM TRA USERID
            // =========================================================
            if (!delPatron.UserID.HasValue)
            {
                // Xóa ảnh nếu có
                if (!string.IsNullOrEmpty(delPatron.ImagePatron))
                {
                    DeletePhoto(delPatron.ImagePatron);
                }

                _context.Patrons.Remove(delPatron);

                await _context.SaveChangesAsync();

                TempData["Success"] =
                    "Đã xóa độc giả và ảnh thành công.";

                return RedirectToAction(nameof(Index));
            }

            int userId = delPatron.UserID.Value;

            // =========================================================
            // KHÔNG CHO XÓA TÀI KHOẢN ĐANG ĐĂNG NHẬP
            // =========================================================
            var currentUserId =
                User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (currentUserId == userId.ToString())
            {
                TempData["Error"] =
                    "Bạn không thể xóa tài khoản đang đăng nhập.";

                return RedirectToAction(nameof(Index));
            }

            // =========================================================
            // TRANSACTION
            // =========================================================
            await using var transaction =
                await _context.Database.BeginTransactionAsync();

            try
            {
                // =====================================================
                // 1. LẤY USER
                // =====================================================
                var user = await _context.tblUsers
                    .FirstOrDefaultAsync(u => u.UserID == userId);

                // =====================================================
                // 2. XÓA TẤT CẢ ROLE CỦA USER
                // =====================================================
                var userRoles = await _context.tblUsersRoles
                    .Where(ur => ur.UserID == userId)
                    .ToListAsync();

                if (userRoles.Any())
                {
                    _context.tblUsersRoles.RemoveRange(userRoles);
                }

                // =====================================================
                // 3. XÓA ẢNH PATRON
                // =====================================================
                if (!string.IsNullOrEmpty(delPatron.ImagePatron))
                {
                    DeletePhoto(delPatron.ImagePatron);
                }

                // =====================================================
                // 4. XÓA PATRON
                // =====================================================
                _context.Patrons.Remove(delPatron);

                // =====================================================
                // 4. XÓA USER
                // =====================================================
                if (user != null)
                {
                    _context.tblUsers.Remove(user);
                }

                // =====================================================
                // 5. LƯU
                // =====================================================
                await _context.SaveChangesAsync();

                // =====================================================
                // 6. COMMIT
                // =====================================================
                await transaction.CommitAsync();

                TempData["Success"] =
                    "Đã xóa độc giả, tài khoản User và toàn bộ vai trò thành công.";

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();

                TempData["Error"] =
                    "Không thể xóa độc giả: " + ex.Message;

                return RedirectToAction(nameof(Index));
            }
        }

        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
    tblPatron mn,
    IFormFile? photo)
        {
            // =========================================================
            // 1. KIỂM TRA MÃ ĐỘC GIẢ
            // =========================================================
            if (mn.PatronID > 0 &&
                await _context.Patrons.AnyAsync(
                    t => t.PatronID == mn.PatronID))
            {
                ModelState.AddModelError(
                    "PatronID",
                    "Mã độc giả này đã tồn tại trong hệ thống.");
            }

            // =========================================================
            // 2. KIỂM TRA EMAIL
            // =========================================================

            // KIỂM TRA EMAIL TRÙNG TRONG TÀI KHOẢN USER
            if (!string.IsNullOrWhiteSpace(mn.Email))
            {
                string email = mn.Email.Trim();

                bool emailExistsInUsers = await _context.tblUsers
                    .AnyAsync(u => u.Email != null && u.Email == email);

                bool emailExistsInPatrons = await _context.Patrons
                    .AnyAsync(p => p.Email != null && p.Email == email);

                if (emailExistsInUsers || emailExistsInPatrons)
                {
                    ModelState.AddModelError(
                        nameof(mn.Email),
                        "Email này đã tồn tại trong hệ thống. Vui lòng sử dụng email khác.");
                }
            }


            // =========================================================
            // 3. KIỂM TRA MODEL
            // =========================================================
            if (!ModelState.IsValid)
            {
                return View(mn);
            }

            // =========================================================
            // 4. UPLOAD ẢNH
            // =========================================================
            if (photo != null && photo.Length > 0)
            {
                var imagePath = await SavePhotoAsync(photo);

                if (!ModelState.IsValid)
                {
                    return View(mn);
                }

                mn.ImagePatron = imagePath;
            }

            // =========================================================
            // 5. TRANSACTION
            // =========================================================
            await using var transaction =
                await _context.Database.BeginTransactionAsync();

            try
            {
                // =====================================================
                // 6. TẠO USER
                // =====================================================
                var user = new tblUsers
                {
                    UserName = mn.PatronName,
                    Email = mn.Email,
                    IsActive = mn.IsActive
                };

                var passwordHasher = new PasswordHasher<tblUsers>();

                user.Password = passwordHasher.HashPassword(
                    user,
                    "Patron@123");

                _context.tblUsers.Add(user);

                await _context.SaveChangesAsync();

                // =====================================================
                // 7. TÌM ROLE PATRON
                // =====================================================
                var patronRole = await _context.tblRoles
                    .FirstOrDefaultAsync(r =>
                        r.RoleName == "Patron");

                if (patronRole == null)
                {
                    throw new Exception(
                        "Không tìm thấy Role Patron trong hệ thống.");
                }

                // =====================================================
                // 8. TẠO USER ROLE
                // =====================================================
                var userRole = new tblUsersRoles
                {
                    UserID = user.UserID,
                    RoleID = patronRole.RoleID
                };

                _context.tblUsersRoles.Add(userRole);

                // =====================================================
                // 9. LIÊN KẾT PATRON VỚI USER
                // =====================================================
                mn.UserID = user.UserID;

                // =====================================================
                // 10. TẠO PATRON
                // =====================================================
                _context.Patrons.Add(mn);

                // =====================================================
                // 11. LƯU
                // =====================================================
                await _context.SaveChangesAsync();

                // =====================================================
                // 12. COMMIT
                // =====================================================
                await transaction.CommitAsync();

                TempData["Success"] =
                    "Tạo độc giả, tài khoản và quyền Patron thành công.";

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();

                // Nếu transaction lỗi thì xóa ảnh vừa upload
                if (!string.IsNullOrEmpty(mn.ImagePatron))
                {
                    DeletePhoto(mn.ImagePatron);
                }

                ModelState.AddModelError(
                    "",
                    "Có lỗi xảy ra khi tạo độc giả: " + ex.Message);

                return View(mn);
            }
        }

        public IActionResult Edit(int? id)
        {
            if (id == null || id == 0)
                return NotFound();
            var mn = _context.Patrons.Find(id);
            if (mn == null)
                return NotFound();
            return View(mn);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
    tblPatron mn,
    IFormFile? photo)
        {
            // =========================================================
            // 1. LẤY PATRON HIỆN TẠI
            // =========================================================
            var existingPatron = await _context.Patrons
                .FirstOrDefaultAsync(p => p.PatronID == mn.PatronID);

            if (existingPatron == null)
                return NotFound();


            // KIỂM TRA EMAIL TRÙNG KHI SỬA ĐỘC GIẢ
            if (!string.IsNullOrWhiteSpace(mn.Email))
            {
                string email = mn.Email.Trim();

                // Kiểm tra email của độc giả khác
                bool emailExistsInOtherPatrons = await _context.Patrons
                    .AnyAsync(p =>
                        p.Email != null &&
                        p.Email == email &&
                        p.PatronID != mn.PatronID);

                // Kiểm tra email của tài khoản User khác
                bool emailExistsInOtherUsers = false;

                if (existingPatron.UserID.HasValue)
                {
                    int currentUserId = existingPatron.UserID.Value;

                    emailExistsInOtherUsers = await _context.tblUsers
                        .AnyAsync(u =>
                            u.Email != null &&
                            u.Email == email &&
                            u.UserID != currentUserId);
                }
                else
                {
                    // Hồ sơ không liên kết tài khoản:
                    // mọi tài khoản User đang sử dụng email này đều bị xem là trùng.
                    emailExistsInOtherUsers = await _context.tblUsers
                        .AnyAsync(u => u.Email != null && u.Email == email);
                }

                if (emailExistsInOtherPatrons || emailExistsInOtherUsers)
                {
                    ModelState.AddModelError(
                        nameof(mn.Email),
                        "Email này đã được sử dụng. Vui lòng nhập email khác.");
                }
            }


            // =========================================================
            // 2. KIỂM TRA MODEL
            // =========================================================
            if (!ModelState.IsValid)
            {
                mn.ImagePatron = existingPatron.ImagePatron;
                mn.UserID = existingPatron.UserID;

                return View(mn);
            }

            // =========================================================
            // 3. XỬ LÝ ẢNH
            // =========================================================
            string? oldImage = existingPatron.ImagePatron;

            if (photo != null && photo.Length > 0)
            {
                var newImagePath = await SavePhotoAsync(photo);

                if (!ModelState.IsValid ||
                    string.IsNullOrEmpty(newImagePath))
                {
                    mn.ImagePatron = existingPatron.ImagePatron;
                    mn.UserID = existingPatron.UserID;

                    return View(mn);
                }

                mn.ImagePatron = newImagePath;
            }
            else
            {
                // Không chọn ảnh mới -> giữ ảnh cũ
                mn.ImagePatron = existingPatron.ImagePatron;
            }

            // =========================================================
            // 4. GIỮ USERID
            // =========================================================
            mn.UserID = existingPatron.UserID;

            // =========================================================
            // 5. CẬP NHẬT THÔNG TIN PATRON
            // =========================================================
            existingPatron.PatronName = mn.PatronName;
            existingPatron.Address = mn.Address;
            existingPatron.Phone = mn.Phone;
            existingPatron.Email = mn.Email;
            existingPatron.RegisterDate = mn.RegisterDate;
            existingPatron.IsActive = mn.IsActive;
            existingPatron.ImagePatron = mn.ImagePatron;
            existingPatron.UserID = existingPatron.UserID;

            // =========================================================
            // 6. ĐỒNG BỘ ISACTIVE SANG USER
            // =========================================================
            if (existingPatron.UserID.HasValue)
            {
                var user = await _context.tblUsers
                    .FirstOrDefaultAsync(u =>
                        u.UserID == existingPatron.UserID.Value);

                if (user != null)
                {
                    user.UserName = existingPatron.PatronName;
                    user.Email = existingPatron.Email;

                    // QUAN TRỌNG
                    user.IsActive = existingPatron.IsActive;
                }
            }

            // =========================================================
            // 7. LƯU DATABASE
            // =========================================================
            await _context.SaveChangesAsync();

            // =========================================================
            // 8. XÓA ẢNH CŨ SAU KHI LƯU ẢNH MỚI THÀNH CÔNG
            // =========================================================
            if (photo != null &&
                photo.Length > 0 &&
                !string.IsNullOrEmpty(oldImage) &&
                oldImage != mn.ImagePatron)
            {
                DeletePhoto(oldImage);
            }

            TempData["Success"] =
                "Cập nhật thông tin độc giả và tài khoản User thành công.";

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var patron = await _context.Patrons
                .FirstOrDefaultAsync(p => p.PatronID == id);

            if (patron == null)
                return NotFound();

            // Đảo trạng thái Patron
            patron.IsActive = !patron.IsActive;

            // Nếu Patron có liên kết User
            if (patron.UserID.HasValue)
            {
                var user = await _context.tblUsers
                    .FirstOrDefaultAsync(u => u.UserID == patron.UserID.Value);

                if (user != null)
                {
                    // Đồng bộ trạng thái User
                    user.IsActive = patron.IsActive;
                }
            }

            await _context.SaveChangesAsync();

            TempData["Success"] = patron.IsActive
                ? "Đã chuyển độc giả sang trạng thái Hoạt động."
                : "Đã chuyển độc giả sang trạng thái Không hoạt động.";

            return RedirectToAction(nameof(Index));
        }

        private async Task<string?> SavePhotoAsync(IFormFile photo)
        {
            string[] allowedExtensions =
            {
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".webp"
    };

            const long maxFileSize = 5 * 1024 * 1024; // 5MB

            // =========================================================
            // KIỂM TRA ĐỊNH DẠNG
            // =========================================================
            var extension = Path
                .GetExtension(photo.FileName)
                .ToLowerInvariant();

            if (!allowedExtensions.Contains(extension))
            {
                ModelState.AddModelError(
                    "Photo",
                    "Ảnh phải có định dạng JPG, JPEG, PNG, GIF hoặc WEBP.");

                return null;
            }

            // =========================================================
            // KIỂM TRA DUNG LƯỢNG
            // =========================================================
            if (photo.Length > maxFileSize)
            {
                ModelState.AddModelError(
                    "Photo",
                    "Dung lượng ảnh không được vượt quá 5MB.");

                return null;
            }

            // =========================================================
            // KIỂM TRA CONTENT TYPE
            // =========================================================
            if (string.IsNullOrWhiteSpace(photo.ContentType) ||
                !photo.ContentType.StartsWith(
                    "image/",
                    StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(
                    "Photo",
                    "File được chọn không phải là hình ảnh.");

                return null;
            }

            // =========================================================
            // THƯ MỤC LƯU ẢNH
            // wwwroot/uploads/patrons
            // =========================================================
            var uploadFolder = Path.Combine(
                _environment.WebRootPath,
                "uploads",
                "patrons");

            if (!Directory.Exists(uploadFolder))
            {
                Directory.CreateDirectory(uploadFolder);
            }

            // =========================================================
            // TẠO TÊN FILE MỚI
            // =========================================================
            var fileName =
                $"{Guid.NewGuid():N}{extension}";

            var filePath = Path.Combine(
                uploadFolder,
                fileName);

            // =========================================================
            // LƯU FILE
            // =========================================================
            await using (var stream =
                new FileStream(filePath, FileMode.Create))
            {
                await photo.CopyToAsync(stream);
            }

            // =========================================================
            // TRẢ VỀ ĐƯỜNG DẪN LƯU DATABASE
            // =========================================================
            return $"/uploads/patrons/{fileName}";
        }

        private void DeletePhoto(string? photoPath)
        {
            if (string.IsNullOrWhiteSpace(photoPath))
                return;

            const string folder = "/uploads/patrons/";

            // Chỉ cho phép xóa ảnh trong thư mục của Patron
            if (!photoPath.StartsWith(
                folder,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var relativePath = photoPath
                .TrimStart('/')
                .Replace(
                    '/',
                    Path.DirectorySeparatorChar);

            var filePath = Path.Combine(
                _environment.WebRootPath,
                relativePath);

            try
            {
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }
            }
            catch
            {
                // Không để lỗi xóa ảnh làm hỏng thao tác cập nhật Patron
            }
        }
    }
}


